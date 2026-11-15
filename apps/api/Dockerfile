# Myna API container. Multi-stage, minimal runtime, non-root, health check, no secrets baked in (all configuration comes from the environment).
# Build from the apps/api folder:  docker build -t myna-api:0.1.0 .
FROM node:22-bookworm-slim AS build
WORKDIR /app
COPY package.json package-lock.json ./
RUN npm ci
COPY tsconfig.json tsconfig.build.json ./
COPY src ./src
RUN npm run build && npm prune --omit=dev

FROM node:22-bookworm-slim AS run
ENV NODE_ENV=production
WORKDIR /app
COPY --from=build /app/node_modules ./node_modules
COPY --from=build /app/dist ./dist
COPY package.json ./
COPY drizzle ./drizzle
USER node
EXPOSE 3000
HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 \
  CMD node -e "fetch('http://127.0.0.1:'+(process.env.PORT||3000)+'/health').then(r=>process.exit(r.ok?0:1)).catch(()=>process.exit(1))"
# Apply migrations first (a separate step with the owner connection): docker run --rm --env-file .env myna-api node dist/db/migrate-cli.js
CMD ["node", "dist/server.js"]
