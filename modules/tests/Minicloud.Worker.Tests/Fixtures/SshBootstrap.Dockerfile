FROM debian:12-slim
RUN apt-get update \
    && apt-get install -y --no-install-recommends openssh-server ca-certificates \
    && mkdir -p /root/.ssh /run/sshd \
    && rm -rf /var/lib/apt/lists/*
