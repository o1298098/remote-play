#!/bin/sh

# 使用环境变量替换 nginx 配置中的占位符
API_PROXY_URL="${API_PROXY_URL:-http://localhost}"

# 将特殊字符进行转义，避免 sed 替换出错
ESCAPED_API_PROXY_URL=$(printf '%s\n' "${API_PROXY_URL}" | sed -e 's/[\/&]/\\&/g')

sed -i "s|__API_PROXY_URL__|${ESCAPED_API_PROXY_URL}|g" /etc/nginx/conf.d/default.conf

# 启动 nginx
nginx -g 'daemon off;'