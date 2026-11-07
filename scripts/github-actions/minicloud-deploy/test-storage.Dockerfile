FROM python:3.12-slim
RUN apt-get update && apt-get install -y --no-install-recommends e2fsprogs && rm -rf /var/lib/apt/lists/*
WORKDIR /test
COPY storage-volume.py test-storage-loopback.py ./
ENV MINICLOUD_STORAGE_LOOPBACK_TEST=1
CMD ["python3", "test-storage-loopback.py"]
