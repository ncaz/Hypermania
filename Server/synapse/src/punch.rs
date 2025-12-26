use std::{collections::HashMap, net::SocketAddr, time::Duration};
use tokio::{
    net::UdpSocket,
    time::{self, Instant},
};

use crate::{
    AppState, ClientId,
    utils::{UdpClientState, parse_client_id},
};

#[repr(u8)]
enum OutgoingPacketType {
    FoundPeer = 0x1,
    WaitingPeer = 0x2,
}

fn encode_waiting<'a>(out: &'a mut [u8]) -> &'a [u8] {
    out[0] = OutgoingPacketType::WaitingPeer as u8;
    &out[..1]
}

fn encode_socket<'a>(peer: SocketAddr, out: &'a mut [u8]) -> &'a [u8] {
    out[0] = OutgoingPacketType::FoundPeer as u8;
    match peer.ip() {
        std::net::IpAddr::V4(v4) => {
            // total 8 bytes
            out[1] = 4;
            out[2..6].copy_from_slice(&v4.octets());
            out[6..8].copy_from_slice(&peer.port().to_be_bytes());
            &out[..8]
        }
        std::net::IpAddr::V6(v6) => {
            // total 20 bytes
            out[1] = 6;
            out[2..18].copy_from_slice(&v6.octets());
            out[18..20].copy_from_slice(&peer.port().to_be_bytes());
            &out[..20]
        }
    }
}

pub async fn punch_coordinator(bind: SocketAddr, st: AppState) -> anyhow::Result<()> {
    const RX_BUF_SIZE: usize = 2048;
    const TX_BUF_SIZE: usize = 64;
    const STALE_AFTER: Duration = Duration::from_secs(60);
    const CLEANUP_EVERY: Duration = Duration::from_secs(5);

    let sock = UdpSocket::bind(bind).await?;
    let mut rx = [0u8; RX_BUF_SIZE];
    let mut tx = [0u8; TX_BUF_SIZE];

    let mut punch_clients: HashMap<ClientId, UdpClientState> = HashMap::new();
    let mut addr_to_client: HashMap<SocketAddr, ClientId> = HashMap::new();

    let mut cleanup_tick = time::interval(CLEANUP_EVERY);
    cleanup_tick.set_missed_tick_behavior(time::MissedTickBehavior::Delay);
    cleanup_tick.tick().await;
    let mut stale_ids = Vec::new();

    loop {
        tokio::select! {
            biased;
            _ = cleanup_tick.tick() => {
                let now = Instant::now();
                for (&client_id, ep) in punch_clients.iter() {
                    if now.duration_since(ep.last_seen) > STALE_AFTER {
                        stale_ids.push(client_id);
                    }
                }
                for client_id in stale_ids.iter() {
                    if let Some(ep) = punch_clients.remove(&client_id) {
                        addr_to_client.remove(&ep.udp_addr);
                    }
                }
                tracing::debug!("Cleaned {} stale punch clients", stale_ids.len());

                stale_ids.clear();
            }

            res = sock.recv_from(&mut rx) => {
                let (n, src) = match res {
                    Ok(v) => v,
                    Err(_) => continue,
                };

                let Some(client_id) = parse_client_id(&rx[..n]) else {
                    continue;
                };

                tracing::debug!("Received punch from client {} from address {}", client_id, src);

                match punch_clients.get_mut(&client_id) {
                    Some(e) => {
                        if e.udp_addr != src {
                            tracing::debug!("Punch client {client_id} migrated from address {} to {}", e.udp_addr, src);

                            addr_to_client.remove(&e.udp_addr);
                            addr_to_client.insert(src, client_id);
                            e.udp_addr = src;
                        }
                        e.last_seen = Instant::now();
                    }
                    None => {
                        punch_clients.insert(
                            client_id,
                            UdpClientState {
                                udp_addr: src,
                                last_seen: Instant::now(),
                            },
                        );
                        addr_to_client.insert(src, client_id);
                    }
                }

                let maybe_pairs: Option<[(SocketAddr, SocketAddr); 2]> = {
                    let inner = st.inner.read().await;
                    let Some(client) = inner.clients.get(&client_id) else {
                        continue;
                    };
                    let Some(room) = inner.rooms.get(&client.room) else {
                        continue;
                    };
                    let Some(guest_id) = room.client else {
                        continue;
                    };
                    let host_id = room.host;
                    let Some(host_punch) = punch_clients.get(&host_id) else {
                        continue;
                    };
                    let Some(guest_punch) = punch_clients.get(&guest_id) else {
                        continue;
                    };
                    Some([
                        (host_punch.udp_addr, guest_punch.udp_addr),
                        (guest_punch.udp_addr, host_punch.udp_addr),
                    ])
                };

                if let Some(pairs) = maybe_pairs {
                    for (dst, peer) in pairs {
                        tracing::debug!("Forwarding punch peer {} for client {}", peer, client_id);

                        let pkt = encode_socket(peer, &mut tx);
                        let _ = sock.send_to(pkt, dst).await;
                    }
                } else {
                    let pkt = encode_waiting(&mut tx);
                    let _ = sock.send_to(pkt, src).await;
                }
            }
        }
    }
}
