use std::net::SocketAddr;

use tokio::{net::UdpSocket, time::Instant};

use crate::{
    AppState,
    utils::{UdpClientState, parse_client_id},
};

#[repr(u8)]
enum IncomingPacketType {
    Bind = 0x1,
    Relay = 0x3,
}

#[repr(u8)]
enum OutgoingPacketType {
    FoundPeer = 0x1,
    WaitingPeer = 0x2,
    _Relay = 0x3,
}

fn encode_waiting<'a>(out: &'a mut [u8]) -> &'a [u8] {
    out[0] = OutgoingPacketType::WaitingPeer as u8;
    &out[..1]
}

fn encode_socket<'a>(peer: SocketAddr, out: &'a mut [u8]) -> &'a [u8] {
    out[0] = OutgoingPacketType::FoundPeer as u8;
    match peer.ip() {
        std::net::IpAddr::V4(v4) => {
            out[1] = 4;
            out[2..6].copy_from_slice(&v4.octets());
            out[6..8].copy_from_slice(&peer.port().to_be_bytes());
            &out[..8]
        }
        std::net::IpAddr::V6(v6) => {
            out[1] = 6;
            out[2..18].copy_from_slice(&v6.octets());
            out[18..20].copy_from_slice(&peer.port().to_be_bytes());
            &out[..20]
        }
    }
}

pub async fn udp_server(bind: SocketAddr, st: AppState) -> anyhow::Result<()> {
    const RX_BUF_SIZE: usize = 2048;
    const TX_BUF_SIZE: usize = 64;

    let sock = UdpSocket::bind(bind).await?;
    let mut rx = [0u8; RX_BUF_SIZE];
    let mut tx = [0u8; TX_BUF_SIZE];

    loop {
        let (n, src) = sock.recv_from(&mut rx).await?;
        if n < 1 {
            continue;
        }

        let pkt_type = rx[0];
        match pkt_type {
            x if x == IncomingPacketType::Bind as u8 => {
                // Expect: [0]=Bind, rest = client_id payload
                let Some(client_id) = parse_client_id(&rx[1..n]) else {
                    continue;
                };

                tracing::debug!(
                    "Received udp bind from client {} from address {}",
                    client_id,
                    src
                );

                // Register/update address
                {
                    let now = Instant::now();
                    let mut udp_state = st.udp_state.write().await;
                    match udp_state.udp_addrs.remove(&client_id) {
                        Some(mut ep) => {
                            if ep.udp_addr != src {
                                let old = ep.udp_addr;
                                tracing::debug!(
                                    "Udp client {client_id} migrated from address {} to {}",
                                    old,
                                    src
                                );

                                udp_state.addr_to_client.remove(&old);
                                udp_state.addr_to_client.insert(src, client_id);
                                ep.udp_addr = src;
                            }
                            ep.last_seen = now;
                            udp_state.udp_addrs.insert(client_id, ep);
                        }
                        None => {
                            udp_state.udp_addrs.insert(
                                client_id,
                                UdpClientState {
                                    udp_addr: src,
                                    last_seen: now,
                                },
                            );
                            udp_state.addr_to_client.insert(src, client_id);
                        }
                    }
                }

                // If we can pair, send FoundPeer to both, else Waiting to src
                let maybe_pairs: Option<[(SocketAddr, SocketAddr); 2]> = {
                    let room_state = st.room_state.read().await;
                    let udp_state = st.udp_state.read().await;

                    let Some(client) = room_state.clients.get(&client_id) else {
                        continue;
                    };
                    let Some(room) = room_state.rooms.get(&client.room) else {
                        continue;
                    };
                    let Some(guest_id) = room.client else {
                        continue;
                    };
                    let host_id = room.host;
                    let Some(host_udp) = udp_state.udp_addrs.get(&host_id) else {
                        continue;
                    };
                    let Some(guest_udp) = udp_state.udp_addrs.get(&guest_id) else {
                        continue;
                    };
                    Some([
                        (host_udp.udp_addr, guest_udp.udp_addr),
                        (guest_udp.udp_addr, host_udp.udp_addr),
                    ])
                };

                if let Some(pairs) = maybe_pairs {
                    for (dst, peer) in pairs {
                        tracing::debug!(
                            "Forwarding punch peer {} for client {} to dst {}",
                            peer,
                            client_id,
                            dst
                        );
                        let pkt = encode_socket(peer, &mut tx);
                        let _ = sock.send_to(pkt, dst).await;
                    }
                } else {
                    let pkt = encode_waiting(&mut tx);
                    let _ = sock.send_to(pkt, src).await;
                }
            }

            x if x == IncomingPacketType::Relay as u8 => {
                // Relay payload is forwarded as-is (including the Relay byte) to the peer.
                tracing::debug!("Received relay packet from address {}", src);

                // Map src -> client_id (must have been registered via Bind)
                let (client_id, peer_addr) = {
                    let mut udp_state = st.udp_state.write().await;

                    let Some(client_id) = udp_state.addr_to_client.get(&src).copied() else {
                        // not registered
                        continue;
                    };

                    if let Some(ep) = udp_state.udp_addrs.get_mut(&client_id) {
                        ep.last_seen = Instant::now();
                    } else {
                        continue;
                    }

                    // Find peer id from room state, then peer udp addr from udp_state
                    let peer_id = {
                        let room_state = st.room_state.read().await;
                        let Some(peer) = room_state.get_peer(client_id) else {
                            continue;
                        };
                        peer
                    };

                    let Some(peer_ep) = udp_state.udp_addrs.get(&peer_id) else {
                        continue;
                    };

                    (client_id, peer_ep.udp_addr)
                };

                tracing::debug!(
                    "Forwarding relay packet from client {} to addr {}",
                    client_id,
                    peer_addr
                );
                let _ = sock.send_to(&rx[..n], peer_addr).await;
            }

            _ => {
                // Unknown packet type
                continue;
            }
        }
    }
}
