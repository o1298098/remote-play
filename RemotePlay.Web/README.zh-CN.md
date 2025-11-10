# Remote Play Web 客户端
[English](README.md) | 中文

一个使用 React、shadcn/ui 与 Tailwind CSS 构建的远程游戏串流 Web 客户端。

## 功能特性

- 🎮 **用户认证**：提供登录、注册与会话管理能力。
- 📱 **设备管理**：浏览和管理已连接的 PlayStation 设备。
- 🎨 **现代化界面**：基于 shadcn/ui 的组件库实现一致的视觉体验。
- 🌙 **深浅色模式**：一键切换暗色与浅色主题。
- 📱 **响应式布局**：自适应桌面与移动端屏幕尺寸。

## 技术栈

- **React 18**：组件化 UI 框架。
- **TypeScript**：提供类型安全与更佳的开发体验。
- **Vite**：极速开发与构建工具。
- **React Router**：管理路由与受保护页面。
- **Tailwind CSS**：实用类优先的样式方案。
- **shadcn/ui**：构建在 Radix UI 之上的可组合 UI 组件。
- **Radix UI**：无样式基础组件，用于实现可访问性。

## 快速开始

### 环境配置

1. 复制 `.env.example` 为 `.env`：
   ```bash
   cp .env.example .env
   ```
2. 按后端实际地址更新 `VITE_API_BASE_URL`：
   ```env
   VITE_API_BASE_URL=http://localhost:5000/api
   ```

### 安装依赖

```bash
npm install
```

### 开发模式

```bash
npm run dev
```

应用默认在 `http://localhost:5173` 启动。

### 构建生产版本

```bash
npm run build
```

### 预览生产构建

```bash
npm run preview
```

## 项目结构

```
remoteplay.web/
├── src/
│   ├── components/     # UI 组件
│   │   └── ui/         # shadcn/ui 组件实现
│   ├── hooks/          # 自定义 React Hooks
│   ├── lib/            # 工具函数
│   ├── pages/          # 页面组件
│   ├── App.tsx         # 主应用组件
│   ├── main.tsx        # 应用入口
│   └── index.css       # 全局样式
├── public/             # 静态资源
├── index.html          # HTML 模板
└── package.json        # 项目配置
```

## 页面说明

### 登录页 (`/login`)
- 用户登录界面，支持邮箱和密码登录。
- 提供跳转注册页链接。

### 注册页 (`/register`)
- 新用户注册界面，包含姓名、邮箱、密码与确认密码字段。
- 内建密码复杂度校验。
- 提供跳转登录页链接。

### 设备管理页 (`/devices`)
- 列出已注册的设备，显示在线状态、名称、类型与 IP 地址。
- 支持连接设备与添加新设备操作。

## 开发说明

### 认证系统

已集成后端认证 API：

1. **注册**：调用 `/api/auth/register`，支持用户名、邮箱、密码注册。
2. **登录**：调用 `/api/auth/login`，支持用户名或邮箱登录。
3. **用户信息管理**：在全局状态与 `localStorage` 中持久化用户信息。
   - Token 存储在 `localStorage` 的 `auth_token` 键。
   - 用户信息存储在 `localStorage` 的 `user_data` 键。
   - 使用 `useAuth` Hook 访问与更新认证状态。

### API 集成

- ✅ `src/pages/Login.tsx`：完成登录 API 集成。
- ✅ `src/pages/Register.tsx`：完成注册 API 集成。
- `src/lib/api.ts`：统一的 API 客户端工具，封装认证相关请求。

## 许可证

MIT


