FROM rust:1.90-bookworm AS builder
WORKDIR /app

COPY Cargo.toml Cargo.lock ./
COPY src ./src
RUN cargo build --release

FROM debian:bookworm-slim
RUN apt-get update && apt-get install -y ca-certificates && rm -rf /var/lib/apt/lists/*
WORKDIR /app

COPY --from=builder /app/target/release/synapse /app/server

EXPOSE 9000/tcp
EXPOSE 9001/udp

CMD ["/app/server", "--http-port", "9000", "--relay-port", "9002"]

