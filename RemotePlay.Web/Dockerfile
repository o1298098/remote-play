# 构建阶段
FROM node:20-alpine AS build

WORKDIR /app

# 复制 package 文件
COPY package*.json ./

# 安装依赖
RUN npm ci

# 复制源代码
COPY . .

# 构建应用
RUN npm run build

# 生产阶段 - 使用 nginx 提供静态文件
FROM nginx:alpine

# 默认 API_PROXY_URL，可在运行时通过 -e 覆盖
ENV API_PROXY_URL=http://localhost

# 复制构建产物到 nginx
COPY --from=build /app/dist /usr/share/nginx/html

# 复制 nginx 配置和启动脚本
COPY docker/nginx.conf /etc/nginx/conf.d/default.conf
COPY docker/start.sh /docker/start.sh

# 确保启动脚本可执行
RUN chmod +x /docker/start.sh

# 暴露端口
EXPOSE 80

# 使用自定义启动脚本以便在启动时替换环境变量
CMD ["/docker/start.sh"]

