use axum::{
    Json,
    extract::rejection::JsonRejection,
    http::StatusCode,
    response::{IntoResponse, Response},
};
use serde::Serialize;

#[derive(Debug)]
pub enum ApiError {
    BadRequest(&'static str),
    NotFound(&'static str),
    Conflict(&'static str),
    Internal(&'static str),
    Json(JsonRejection),
}

#[derive(Serialize)]
struct ErrorBody {
    code: &'static str,
    message: String,
}

impl IntoResponse for ApiError {
    fn into_response(self) -> Response {
        let (status, code, msg) = match self {
            ApiError::BadRequest(m) => (StatusCode::BAD_REQUEST, "bad_request", m.to_string()),
            ApiError::NotFound(m) => (StatusCode::NOT_FOUND, "not_found", m.to_string()),
            ApiError::Conflict(m) => (StatusCode::CONFLICT, "conflict", m.to_string()),
            ApiError::Internal(m) => (StatusCode::INTERNAL_SERVER_ERROR, "internal", m.to_string()),
            ApiError::Json(rej) => (StatusCode::BAD_REQUEST, "invalid_json", rej.body_text()),
        };

        (status, Json(ErrorBody { code, message: msg })).into_response()
    }
}

impl From<JsonRejection> for ApiError {
    fn from(value: JsonRejection) -> Self {
        ApiError::Json(value)
    }
}

// Optional: map other errors
impl From<anyhow::Error> for ApiError {
    fn from(_: anyhow::Error) -> Self {
        ApiError::Internal("unexpected error")
    }
}

pub type ApiResult<T> = Result<Json<T>, ApiError>;
