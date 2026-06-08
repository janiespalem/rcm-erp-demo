FROM node:20-alpine AS frontend-build
WORKDIR /app/frontend-vite
COPY frontend-vite/package*.json ./
RUN npm ci
COPY frontend-vite/ ./
RUN npm run build

FROM python:3.11-slim
WORKDIR /app

COPY requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt

COPY backend/ ./backend/
COPY templates/ ./templates/
COPY frontend/masonry-kalk.html ./frontend/masonry-kalk.html
COPY alembic.ini ./
COPY migrations/ ./migrations/

COPY --from=frontend-build /app/frontend-vite/dist ./frontend-vite/dist

RUN mkdir -p /app/data backend/uploads backend/static

WORKDIR /app/backend
EXPOSE 8000
CMD ["uvicorn", "main:app", "--host", "0.0.0.0", "--port", "8000"]
