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

export default function Register() {
  const { t } = useTranslation()
  const [formData, setFormData] = useState({
    username: '',
    email: '',
    password: '',
    confirmPassword: '',
  })
  const [isLoading, setIsLoading] = useState(false)
  const { login } = useAuth()
  const navigate = useNavigate()
  const { toast } = useToast()

  const handleChange = (e: React.ChangeEvent<HTMLInputElement>) => {
    setFormData({
      ...formData,
      [e.target.id]: e.target.value,
    })
  }

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault()
    
    // 验证用户名格式（只能包含字母、数字和下划线）
    const usernameRegex = /^[a-zA-Z0-9_]+$/
    if (!usernameRegex.test(formData.username)) {
      toast({
        title: t('auth.register.usernameFormatError'),
        description: t('auth.register.usernameFormatErrorDescription'),
        variant: 'destructive',
      })
      return
    }

    if (formData.username.length < 3) {
      toast({
        title: t('auth.register.usernameTooShort'),
        description: t('auth.register.usernameTooShortDescription'),
        variant: 'destructive',
      })
      return
    }

    if (formData.password !== formData.confirmPassword) {
      toast({
        title: t('auth.register.passwordMismatch'),
        description: t('auth.register.passwordMismatchDescription'),
        variant: 'destructive',
      })
      return
    }

    if (formData.password.length < 8) {
      toast({
        title: t('auth.register.passwordTooShort'),
        description: t('auth.register.passwordTooShortDescription'),
        variant: 'destructive',
      })
      return
    }

    setIsLoading(true)

    try {
      // 调用注册 API
      const response = await authService.register({
        username: formData.username,
        email: formData.email,
        password: formData.password,
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
          title: t('auth.register.success'),
          description: t('auth.register.successDescription'),
        })
        
        navigate('/devices')
      } else {
        throw new Error(response.errorMessage || response.message || t('auth.register.failure'))
      }
    } catch (error) {
      const errorMessage = error instanceof Error ? error.message : t('auth.register.failureDescription')
      toast({
        title: t('auth.register.failure'),
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
          <CardTitle className="text-3xl font-bold">{t('auth.register.title')}</CardTitle>
          <CardDescription className="text-base">
            {t('auth.register.subtitle')}
          </CardDescription>
        </CardHeader>
        <form onSubmit={handleSubmit}>
          <CardContent className="space-y-4">
            <div className="space-y-2">
              <Label htmlFor="username">{t('auth.register.username')}</Label>
              <Input
                id="username"
                type="text"
                placeholder={t('auth.register.usernamePlaceholder')}
                value={formData.username}
                onChange={handleChange}
                required
                disabled={isLoading}
                minLength={3}
                maxLength={50}
                pattern="^[a-zA-Z0-9_]+$"
              />
              <p className="text-xs text-muted-foreground">
                {t('auth.register.usernameHint')}
              </p>
            </div>
            <div className="space-y-2">
              <Label htmlFor="email">{t('auth.register.email')}</Label>
              <Input
                id="email"
                type="email"
                placeholder={t('auth.register.emailPlaceholder')}
                value={formData.email}
                onChange={handleChange}
                required
                disabled={isLoading}
              />
            </div>
            <div className="space-y-2">
              <Label htmlFor="password">{t('auth.register.password')}</Label>
              <Input
                id="password"
                type="password"
                placeholder={t('auth.register.passwordPlaceholder')}
                value={formData.password}
                onChange={handleChange}
                required
                disabled={isLoading}
                minLength={8}
              />
              <p className="text-xs text-muted-foreground">
                {t('auth.register.passwordHint')}
              </p>
            </div>
            <div className="space-y-2">
              <Label htmlFor="confirmPassword">{t('auth.register.confirmPassword')}</Label>
              <Input
                id="confirmPassword"
                type="password"
                placeholder={t('auth.register.confirmPasswordPlaceholder')}
                value={formData.confirmPassword}
                onChange={handleChange}
                required
                disabled={isLoading}
                minLength={8}
              />
            </div>
          </CardContent>
          <CardFooter className="flex flex-col space-y-4">
            <Button
              type="submit"
              className="w-full"
              disabled={isLoading}
            >
              {isLoading ? t('auth.register.registering') : t('auth.register.registerButton')}
            </Button>
            <div className="text-sm text-center text-muted-foreground">
              {t('auth.register.hasAccount')}{' '}
              <Link
                to="/login"
                className="text-primary hover:underline font-medium"
              >
                {t('auth.register.loginLink')}
              </Link>
            </div>
          </CardFooter>
        </form>
      </Card>
    </div>
  )
}

