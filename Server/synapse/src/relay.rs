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
pub enum RelaySendType {
    Bind = 0x1,
    Relay = 0x2,
}

impl TryFrom<u8> for RelaySendType {
    type Error = ();

    fn try_from(value: u8) -> Result<Self, Self::Error> {
        match value {
            0x1 => Ok(RelaySendType::Bind),
            0x2 => Ok(RelaySendType::Relay),
            _ => Err(()),
        }
    }
}

#[derive(Debug)]
pub enum RelaySend<'a> {
    Bind(ClientId),
    Relay(&'a [u8]),
}

impl<'a> RelaySend<'a> {
    pub fn parse(buf: &'a mut [u8]) -> Option<Self> {
        if buf.is_empty() {
            return None;
        }
        let msg = match RelaySendType::try_from(buf[0]).ok()? {
            RelaySendType::Bind => {
                let client_id = parse_client_id(&buf[1..])?;
                RelaySend::Bind(client_id)
            }
            RelaySendType::Relay => RelaySend::Relay(buf),
        };
        Some(msg)
    }
}

pub async fn relay_server(bind: SocketAddr, st: AppState) -> anyhow::Result<()> {
    const RX_BUF_SIZE: usize = 2048;
    const STALE_AFTER: Duration = Duration::from_secs(60);
    const CLEANUP_EVERY: Duration = Duration::from_secs(5);

    let sock = UdpSocket::bind(bind).await?;
    let mut rx = [0u8; RX_BUF_SIZE];

    let mut relay_clients: HashMap<ClientId, UdpClientState> = HashMap::new();
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
                for (&client_id, ep) in relay_clients.iter() {
                    if now.duration_since(ep.last_seen) > STALE_AFTER {
                        stale_ids.push(client_id);
                    }
                }
                for client_id in stale_ids.iter() {
                    if let Some(ep) = relay_clients.remove(&client_id) {
                        addr_to_client.remove(&ep.udp_addr);
                    }
                }
                tracing::debug!("Cleaned {} stale relay clients", stale_ids.len());

                stale_ids.clear();
            }

            res = sock.recv_from(&mut rx) => {
                let (n, src) = match res {
                    Ok(v) => v,
                    Err(_) => continue,
                };

                let Some(pkt) = RelaySend::parse(&mut rx[..n]) else {
                    continue;
                };

                match pkt {
                    RelaySend::Bind(client_id) => {
                        tracing::debug!("Received relay bind request for {} from address {}", client_id, src);

                        match relay_clients.get_mut(&client_id) {
                            Some(e) => {
                                if e.udp_addr != src {
                                    tracing::debug!("Relay client {client_id} migrated from address {} to {}", e.udp_addr, src);

                                    addr_to_client.remove(&e.udp_addr);
                                    addr_to_client.insert(src, client_id);
                                    e.udp_addr = src;
                                }
                                e.last_seen = Instant::now();
                            }
                            None => {
                                relay_clients.insert(
                                    client_id,
                                    UdpClientState {
                                        udp_addr: src,
                                        last_seen: Instant::now(),
                                    },
                                );
                                addr_to_client.insert(src, client_id);
                            }
                        }
                        // respond with bindack
                        let _ = sock.send_to(&rx[0..1], src).await;
                    }

                    RelaySend::Relay(buf) => {
                        tracing::debug!("Received relay request from address {}", src);

                        let Some(client_id) = addr_to_client.get(&src).copied() else {
                            continue;
                        };
                        let Some(ep) = relay_clients.get_mut(&client_id) else {
                            continue;
                        };
                        ep.last_seen = Instant::now();

                        let peer_ep = {
                            let inner = st.inner.read().await;
                            let Some(peer) = inner.get_peer(client_id) else {
                                continue;
                            };
                            let Some(peer_ep) = relay_clients.get_mut(&peer) else {
                                continue;
                            };
                            peer_ep
                        };
                        tracing::debug!("Forwarding relay request from client {} to addr {}", client_id, peer_ep.udp_addr);
                        // forward entire datagram (includes 0x2 relay id)
                        let _ = sock.send_to(buf, peer_ep.udp_addr).await;
                    }
                }
            }
        }
    }
}
