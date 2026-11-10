FROM postgres:16-alpine
RUN apk add --no-cache python3 openssl
COPY scripts/database-platform/ /app/
WORKDIR /app
ENV PYTHONDONTWRITEBYTECODE=1 PYTHONUNBUFFERED=1
EXPOSE 6211
ENTRYPOINT ["python3", "/app/worker.py"]
