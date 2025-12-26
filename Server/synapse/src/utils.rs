use std::net::SocketAddr;

use tokio::time::Instant;

use crate::ClientId;

pub struct UdpClientState {
    pub udp_addr: SocketAddr,
    pub last_seen: Instant,
}

#[inline]
pub fn parse_client_id(buf: &[u8]) -> Option<ClientId> {
    if buf.len() < 16 {
        return None;
    }
    let mut id_bytes = [0u8; 16];
    id_bytes.copy_from_slice(&buf[..16]);
    Some(u128::from_be_bytes(id_bytes))
}
