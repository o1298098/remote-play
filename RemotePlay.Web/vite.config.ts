import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'
import { VitePWA } from 'vite-plugin-pwa'
import path from 'path'

// https://vitejs.dev/config/
export default defineConfig({
  plugins: [
    react(),
    VitePWA({
      // 新版 SW 安装后直接接管，但不自动刷新页面，避免打断正在进行的串流；下次打开或刷新时生效
      registerType: 'autoUpdate',
      injectRegister: 'script',
      includeAssets: ['favicon.ico', 'apple-touch-icon-180x180.png', 'logo.svg'],
      manifest: {
        name: 'Remote Play - 远程游戏串流',
        short_name: 'Remote Play',
        description: 'PlayStation 远程游戏串流',
        lang: 'zh-CN',
        start_url: '/',
        scope: '/',
        display: 'standalone',
        background_color: '#020817',
        theme_color: '#2563eb',
        icons: [
          { src: 'pwa-64x64.png', sizes: '64x64', type: 'image/png' },
          { src: 'pwa-192x192.png', sizes: '192x192', type: 'image/png' },
          { src: 'pwa-512x512.png', sizes: '512x512', type: 'image/png' },
          { src: 'maskable-icon-512x512.png', sizes: '512x512', type: 'image/png', purpose: 'maskable' },
        ],
      },
      workbox: {
        globPatterns: ['**/*.{js,css,html,ico,png,svg,jpg,woff2}'],
        cleanupOutdatedCaches: true,
        // 后端接口与 SignalR 必须直连网络，不能被 SPA 回退到 index.html
        navigateFallbackDenylist: [/^\/api/, /^\/hubs/, /^\/swagger/],
      },
    }),
  ],
  resolve: {
    alias: {
      '@': path.resolve(__dirname, './src'),
    },
  },
  server: {
    host: '0.0.0.0', // 允许外部访问
    port: 5173, // 默认端口
    strictPort: false, // 如果端口被占用，尝试下一个可用端口
  },
})
