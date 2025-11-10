# Remote Play Web Client
English | [中文](README.zh-CN.md)

A remote gaming streaming client built with React, shadcn/ui, and Tailwind CSS.

## Feature Highlights

- 🎮 **User authentication**: Sign-in, sign-up, and session handling.
- 📱 **Device management**: Browse and control connected PlayStation consoles.
- 🎨 **Modern UI**: Consistent design powered by shadcn/ui.
- 🌙 **Light & dark themes**: Toggle between themes in one click.
- 📱 **Responsive layout**: Works seamlessly on desktop and mobile.

## Tech Stack

- **React 18** – Component-driven UI framework.
- **TypeScript** – Static typing for safer code.
- **Vite** – Fast dev/build toolchain.
- **React Router** – Client-side routing and protected views.
- **Tailwind CSS** – Utility-first styling approach.
- **shadcn/ui** – Composable UI primitives built on Radix.
- **Radix UI** – Accessible, unstyled component primitives.

## Getting Started

### Environment

1. Copy `.env.example` to `.env`:
   ```bash
   cp .env.example .env
   ```
2. Update `VITE_API_BASE_URL` to point at your backend API:
   ```env
   VITE_API_BASE_URL=http://localhost:5000/api
   ```

### Install dependencies

```bash
npm install
```

### Development mode

```bash
npm run dev
```

The dev server boots at `http://localhost:5173`.

### Production build

```bash
npm run build
```

### Preview production build

```bash
npm run preview
```

## Project Structure

```
remoteplay.web/
├── src/
│   ├── components/     # UI components
│   │   └── ui/         # shadcn/ui implementations
│   ├── hooks/          # Custom React hooks
│   ├── lib/            # Utility helpers
│   ├── pages/          # Route-level components
│   ├── App.tsx         # Root application component
│   ├── main.tsx        # Entry point
│   └── index.css       # Global styles
├── public/             # Static assets
├── index.html          # HTML template
└── package.json        # Project metadata
```

## Pages

### Login (`/login`)
- Email + password login form.
- Link to the registration page.

### Register (`/register`)
- Collects name, email, password, and confirmation.
- Includes password strength validation.
- Link back to the login page.

### Devices (`/devices`)
- Lists registered devices with status, name, type, and IP.
- Start streaming sessions or add new devices.

## Development Notes

### Authentication

Integrated with backend auth APIs:

1. **Sign-up** uses `/api/auth/register`.
2. **Sign-in** uses `/api/auth/login`.
3. **User state** persists in global state and `localStorage`.
   - Tokens stored under `auth_token`.
   - Profile cached under `user_data`.
   - The `useAuth` hook centralizes auth logic.

### API Integration

- ✅ `src/pages/Login.tsx` – Login workflow wired to the API.
- ✅ `src/pages/Register.tsx` – Registration workflow wired to the API.
- `src/lib/api.ts` – API client utilities wrapping auth requests.

## License

MIT

