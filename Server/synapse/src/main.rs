use axum::{
    Json, Router,
    extract::{Path, State},
    routing::post,
};
use serde::{Deserialize, Serialize};
use std::{collections::HashMap, sync::Arc};
use tokio::sync::RwLock;
use tower_http::trace::TraceLayer;
use tracing_subscriber::{layer::SubscriberExt, util::SubscriberInitExt};

use crate::{
    error::{ApiError, ApiResult},
    punch::punch_coordinator,
    relay::relay_server,
};

mod error;
mod punch;
mod relay;
mod utils;

type RoomId = u64;
type ClientId = u128;
type ClientString = String;

#[derive(Clone)]
struct AppState {
    inner: Arc<RwLock<AppStateInner>>,
}

struct AppStateInner {
    rooms: HashMap<RoomId, RoomState>,
    clients: HashMap<ClientId, ClientState>,
}

impl AppStateInner {
    pub fn get_peer(&self, client_id: ClientId) -> Option<ClientId> {
        let client = self.clients.get(&client_id)?;
        let room = self.rooms.get(&client.room)?;
        let guest_id = room.client?;
        let other_id = if room.host == client_id {
            guest_id
        } else {
            room.host
        };
        Some(other_id)
    }
}

#[derive(Default)]
struct RoomState {
    host: ClientId,
    client: Option<ClientId>,
}

struct ClientState {
    room: RoomId,
}

impl ClientState {
    fn new(room_id: RoomId) -> Self {
        Self { room: room_id }
    }
}

#[tokio::main]
async fn main() -> anyhow::Result<()> {
    let rooms = HashMap::new();
    let clients = HashMap::new();
    let inner = Arc::new(RwLock::new(AppStateInner { rooms, clients }));
    let state = AppState { inner };

    tracing_subscriber::registry()
        .with(
            tracing_subscriber::EnvFilter::try_from_default_env().unwrap_or_else(|_| {
                format!(
                    "{}=debug,tower_http=debug,axum::rejection=trace",
                    env!("CARGO_CRATE_NAME")
                )
                .into()
            }),
        )
        .with(tracing_subscriber::fmt::layer())
        .init();

    tokio::spawn(punch_coordinator("0.0.0.0:9000".parse()?, state.clone()));
    tokio::spawn(relay_server("0.0.0.0:9001".parse()?, state.clone()));

    let app = Router::new()
        .layer(TraceLayer::new_for_http())
        .route("/create_room", post(create_room))
        .route("/join_room/{room_id}", post(join_room))
        .route("/leave_room", post(leave_room))
        .with_state(state);

    let listener = tokio::net::TcpListener::bind("0.0.0.0:3000").await?;
    axum::serve(listener, app).await?;
    Ok(())
}

#[derive(Deserialize)]
struct CreateRoomReq {
    client_id: ClientString,
}

#[derive(Serialize)]
struct CreateRoomResp {
    room_id: RoomId,
}

async fn create_room(
    State(st): State<AppState>,
    Json(req): Json<CreateRoomReq>,
) -> ApiResult<CreateRoomResp> {
    let Ok(client_id) = req.client_id.parse::<u128>() else {
        return Err(ApiError::BadRequest("could not parse client id"));
    };
    let mut state = st.inner.write().await;
    let room_id = state.rooms.len() as u64;

    state.rooms.insert(
        room_id,
        RoomState {
            host: client_id,
            client: None,
        },
    );
    state
        .clients
        .entry(client_id)
        .and_modify(|e| e.room = room_id)
        .or_insert(ClientState::new(room_id));

    Ok(Json(CreateRoomResp { room_id }))
}

#[derive(Deserialize)]
struct JoinRoomReq {
    client_id: ClientString,
}

#[derive(Serialize)]
struct JoinRoomResp {}

async fn join_room(
    State(st): State<AppState>,
    Path(room_id): Path<u64>,
    Json(req): Json<JoinRoomReq>,
) -> ApiResult<JoinRoomResp> {
    let Ok(client_id) = req.client_id.parse::<u128>() else {
        return Err(ApiError::BadRequest("could not parse client id"));
    };
    let mut state = st.inner.write().await;

    if state
        .clients
        .get(&client_id)
        .is_some_and(|e| e.room != room_id)
    {
        return Err(ApiError::Conflict("client is already in another room"));
    }
    let Some(room) = state.rooms.get_mut(&room_id) else {
        return Err(ApiError::NotFound("room not found"));
    };
    if room.client.is_some() {
        return Err(ApiError::Conflict("room is full"));
    }

    room.client = Some(client_id);
    state
        .clients
        .entry(client_id)
        .and_modify(|e| e.room = room_id)
        .or_insert(ClientState::new(room_id));

    Ok(Json(JoinRoomResp {}))
}

#[derive(Deserialize)]
struct LeaveRoomReq {
    client_id: ClientString,
}

async fn leave_room(State(st): State<AppState>, Json(req): Json<LeaveRoomReq>) -> ApiResult<()> {
    let Ok(client_id) = req.client_id.parse::<u128>() else {
        return Err(ApiError::BadRequest("could not parse client id"));
    };
    let mut state = st.inner.write().await;
    let Some(client_state) = state.clients.get(&client_id) else {
        return Err(ApiError::NotFound("client not in a room"));
    };
    let cur_room = client_state.room;
    let Some(room) = state.rooms.get_mut(&cur_room) else {
        return Err(ApiError::NotFound("client's room no longer exists"));
    };

    if room.client.is_some_and(|id| id == client_id) {
        // client was the client of the room
        room.client = None;
    } else {
        if client_id != room.host {
            return Err(ApiError::Internal("client's cached room was incorrect"));
        }
        // client was the host of the room
        match room.client {
            // if there was a peer, that peer becomes the host
            Some(peer) => {
                room.host = peer;
                room.client = None;
            }
            // otherwise, the room is empty, and should be removed
            None => {
                state.rooms.remove(&cur_room);
            }
        }
    }
    state.clients.remove(&client_id);
    Ok(Json(()))
}
