import { useState } from 'react'
import { Link, useNavigate } from 'react-router-dom'
import { useTranslation } from 'react-i18next'
import { useAuth } from '@/hooks/use-auth'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Card, CardContent, CardDescription, CardFooter, CardHeader, CardTitle } from '@/components/ui/card'
import { useToast } from '@/hooks/use-toast'
import { Gamepad2 } from 'lucide-react'
import { authService } from '@/service/auth.service'
import { LanguageSwitcher } from '@/components/LanguageSwitcher'

export default function Login() {
  const { t } = useTranslation()
  const [usernameOrEmail, setUsernameOrEmail] = useState('')
  const [password, setPassword] = useState('')
  const [isLoading, setIsLoading] = useState(false)
  const { login } = useAuth()
  const navigate = useNavigate()
  const { toast } = useToast()

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault()
    setIsLoading(true)

    try {
      // 调用登录 API
      const response = await authService.login({
        usernameOrEmail,
        password,
      })

      if (response.success && response.result) {
        const { token, username, email } = response.result
        
        // 先保存 token，然后获取完整用户信息
        login(token, {
          id: username, // 临时 ID
          username,
          email,
          name: username,
        })
        
        // 获取完整的用户信息（包括用户 ID）
        try {
          const userResponse = await authService.getCurrentUser()
          if (userResponse.success && userResponse.result) {
            const userInfo = userResponse.result as any
            login(token, {
              id: userInfo.userId || username,
              username: userInfo.username || username,
              email: userInfo.email || email,
              name: userInfo.username || username,
            })
          }
        } catch (err) {
          // 如果获取用户信息失败，使用已保存的信息继续
          console.warn('获取用户信息失败，使用默认信息', err)
        }
        
        toast({
          title: t('auth.login.success'),
          description: t('auth.login.successDescription'),
        })
        
        navigate('/devices')
      } else {
        throw new Error(response.errorMessage || response.message || t('auth.login.failure'))
      }
    } catch (error) {
      const errorMessage = error instanceof Error ? error.message : t('auth.login.failureDescription')
      toast({
        title: t('auth.login.failure'),
        description: errorMessage,
        variant: 'destructive',
      })
    } finally {
      setIsLoading(false)
    }
  }

  return (
    <div className="min-h-screen flex items-center justify-center bg-gradient-to-br from-blue-50 via-indigo-50 to-purple-50 dark:from-gray-900 dark:via-gray-800 dark:to-gray-900 p-4">
      <div className="absolute top-4 right-4">
        <LanguageSwitcher />
      </div>
      <Card className="w-full max-w-md shadow-2xl">
        <CardHeader className="space-y-1 text-center">
          <div className="flex justify-center mb-4">
            <div className="rounded-full bg-primary/10 p-3">
              <Gamepad2 className="h-8 w-8 text-primary" />
            </div>
          </div>
          <CardTitle className="text-3xl font-bold">Remote Play</CardTitle>
          <CardDescription className="text-base">
            {t('auth.login.subtitle')}
          </CardDescription>
        </CardHeader>
        <form onSubmit={handleSubmit}>
          <CardContent className="space-y-4">
            <div className="space-y-2">
              <Label htmlFor="usernameOrEmail">{t('auth.login.usernameOrEmail')}</Label>
              <Input
                id="usernameOrEmail"
                type="text"
                placeholder={t('auth.login.usernameOrEmailPlaceholder')}
                value={usernameOrEmail}
                onChange={(e) => setUsernameOrEmail(e.target.value)}
                required
                disabled={isLoading}
              />
            </div>
            <div className="space-y-2">
              <Label htmlFor="password">{t('auth.login.password')}</Label>
              <Input
                id="password"
                type="password"
                placeholder={t('auth.login.passwordPlaceholder')}
                value={password}
                onChange={(e) => setPassword(e.target.value)}
                required
                disabled={isLoading}
              />
            </div>
          </CardContent>
          <CardFooter className="flex flex-col space-y-4">
            <Button
              type="submit"
              className="w-full"
              disabled={isLoading}
            >
              {isLoading ? t('auth.login.loggingIn') : t('auth.login.loginButton')}
            </Button>
            <div className="text-sm text-center text-muted-foreground">
              {t('auth.login.noAccount')}{' '}
              <Link
                to="/register"
                className="text-primary hover:underline font-medium"
              >
                {t('auth.login.registerLink')}
              </Link>
            </div>
          </CardFooter>
        </form>
      </Card>
    </div>
  )
}

