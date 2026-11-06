# 构建阶段：只 publish 网关项目（Kernel 作为项目引用随之构建），不构建 tests
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# 先只拷项目文件，命中的 NuGet 还原缓存可跨代码改动复用
COPY src/FishRelief.Gateway/FishRelief.Gateway.csproj src/FishRelief.Gateway/
COPY src/FishRelief.Kernel/FishRelief.Kernel.csproj src/FishRelief.Kernel/
RUN dotnet restore src/FishRelief.Gateway/FishRelief.Gateway.csproj

# demo 清单由 Gateway.csproj 的 None Include 拷入发布产物，必须与 src 一起进构建上下文
COPY src/ src/
COPY manifests/demo/ manifests/demo/
RUN dotnet publish src/FishRelief.Gateway/FishRelief.Gateway.csproj -c Release -o /out --no-restore

# 运行阶段：不锁架构（基础镜像多架构、发布产物为可移植 IL），amd64/arm64 均可直接构建
FROM mcr.microsoft.com/dotnet/aspnet:8.0-bookworm-slim
WORKDIR /app
COPY --from=build /out ./

# 日志卷可写、清单挂载点 /manifests 预建（挂载后只读）；0777 保证任意 UID 运行时可写（容器默认仍以 root 运行，未降权）
RUN mkdir -p /app/logs /manifests \
    && chmod 0777 /app/logs /manifests

# Program.cs 未显式配置 URL，容器内监听端口由该环境变量决定，须与 EXPOSE 保持一致
ENV ASPNETCORE_HTTP_PORTS=8080 \
    ASPNETCORE_ENVIRONMENT=Production

EXPOSE 8080

ENTRYPOINT ["dotnet", "FishRelief.Gateway.dll"]
