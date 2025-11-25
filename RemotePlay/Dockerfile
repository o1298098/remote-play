# 请参阅 https://aka.ms/customizecontainer 以了解如何自定义调试容器，以及 Visual Studio 如何使用此 Dockerfile 生成映像以更快地进行调试。

# 此阶段用于在快速模式(默认为调试配置)下从 VS 运行时
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
USER root

# 安装 ffmpeg - 支持多架构
ARG TARGETARCH
RUN apt-get update && apt-get install -y wget xz-utils \
 && if [ "$TARGETARCH" = "amd64" ]; then \
      wget https://johnvansickle.com/ffmpeg/releases/ffmpeg-release-amd64-static.tar.xz -O ffmpeg.tar.xz && \
      tar -xJf ffmpeg.tar.xz && \
      cp ffmpeg-*-amd64-static/ffmpeg /usr/local/bin/ && \
      rm -rf ffmpeg-*-amd64-static; \
    elif [ "$TARGETARCH" = "arm64" ]; then \
      wget https://johnvansickle.com/ffmpeg/releases/ffmpeg-release-arm64-static.tar.xz -O ffmpeg.tar.xz && \
      tar -xJf ffmpeg.tar.xz && \
      cp ffmpeg-*-arm64-static/ffmpeg /usr/local/bin/ && \
      rm -rf ffmpeg-*-arm64-static; \
    else \
      echo "Unsupported architecture: $TARGETARCH, falling back to apt-get" && \
      apt-get install -y ffmpeg; \
    fi \
 && chmod +x /usr/local/bin/ffmpeg \
 && rm -rf /var/lib/apt/lists/* ffmpeg.tar.xz \
 && ffmpeg -version

WORKDIR /app
EXPOSE 8080
EXPOSE 8081


# 此阶段用于生成服务项目
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["RemotePlay.csproj", "."]
RUN dotnet restore "./RemotePlay.csproj"
COPY . .
WORKDIR "/src/."
RUN dotnet build "./RemotePlay.csproj" -c $BUILD_CONFIGURATION -o /app/build

# 此阶段用于发布要复制到最终阶段的服务项目
FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./RemotePlay.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

# 此阶段在生产中使用，或在常规模式下从 VS 运行时使用(在不使用调试配置时为默认值)
FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "RemotePlay.dll"]