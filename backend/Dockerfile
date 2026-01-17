# Build the project
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build-env
WORKDIR /nzb-webdav
COPY ./ ./
RUN dotnet restore
RUN dotnet publish -c Release -r linux-musl-x64 -o ./publish

# Build the production image
FROM mcr.microsoft.com/dotnet/aspnet:9.0-alpine

# Build info args (passed from Makefile)
ARG NZBDAV_VERSION=dev
ARG NZBDAV_BUILD_DATE=unknown
ARG NZBDAV_GIT_BRANCH=unknown
ARG NZBDAV_GIT_COMMIT=unknown
ARG NZBDAV_GIT_REMOTE=unknown

# Convert to environment variables for runtime access
ENV NZBDAV_VERSION=$NZBDAV_VERSION
ENV NZBDAV_BUILD_DATE=$NZBDAV_BUILD_DATE
ENV NZBDAV_GIT_BRANCH=$NZBDAV_GIT_BRANCH
ENV NZBDAV_GIT_COMMIT=$NZBDAV_GIT_COMMIT
ENV NZBDAV_GIT_REMOTE=$NZBDAV_GIT_REMOTE

EXPOSE 8080
WORKDIR /nzb-webdav
RUN mkdir /config
RUN apk add --no-cache libc6-compat shadow su-exec && rm -rf /var/cache/apk/*
COPY --from=build-env /nzb-webdav/publish ./
COPY entrypoint.sh /entrypoint.sh
RUN chmod +x /entrypoint.sh
CMD ["/entrypoint.sh"]