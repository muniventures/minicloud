FROM python:3.13-alpine
RUN addgroup -g 1000 repository && adduser -D -u 1000 -G repository repository \
    && mkdir -p /app /data/backups && chown 1000:1000 /data/backups
COPY modules/backup-repository/server.py /app/server.py
USER 1000:1000
WORKDIR /app
ENV PYTHONDONTWRITEBYTECODE=1 PYTHONUNBUFFERED=1
EXPOSE 8080
CMD ["python", "/app/server.py"]
