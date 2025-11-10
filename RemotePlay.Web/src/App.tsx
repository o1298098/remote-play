import { BrowserRouter, Routes, Route, Navigate } from 'react-router-dom'
import { Toaster } from '@/components/ui/toaster'
import Login from './pages/auth/Login'
import Register from './pages/auth/Register'
import Devices from './pages/device'
import ControllerMapping from './pages/setting/ControllerMapping'
import OAuthCallback from './pages/auth/OAuthCallback'
import Streaming from './pages/streaming'
import { useAuth } from './hooks/use-auth'
import { AuthProvider } from './hooks/use-auth'
import { ThemeProvider } from './hooks/use-theme'
import { GamepadProvider } from './hooks/use-gamepad'

function ProtectedRoute({ children }: { children: React.ReactNode }) {
  const { isAuthenticated } = useAuth()
  
  if (!isAuthenticated) {
    return <Navigate to="/login" replace />
  }
  
  return <>{children}</>
}

function App() {
  return (
    <ThemeProvider>
      <AuthProvider>
        <GamepadProvider>
          <BrowserRouter>
            <Routes>
              <Route path="/login" element={<Login />} />
              <Route path="/register" element={<Register />} />
              <Route path="/oauth/callback" element={<OAuthCallback />} />
              <Route
                path="/devices"
                element={
                  <ProtectedRoute>
                    <Devices />
                  </ProtectedRoute>
                }
              />
              <Route
                path="/controller-mapping"
                element={
                  <ProtectedRoute>
                    <ControllerMapping />
                  </ProtectedRoute>
                }
              />
              <Route
                path="/streaming"
                element={
                  <ProtectedRoute>
                    <Streaming />
                  </ProtectedRoute>
                }
              />
              <Route path="/" element={<Navigate to="/devices" replace />} />
            </Routes>
            <Toaster />
          </BrowserRouter>
        </GamepadProvider>
      </AuthProvider>
    </ThemeProvider>
  )
}

export default App
