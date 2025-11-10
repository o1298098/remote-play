/**
 * 全局用户状态使用示例
 * 
 * 在任何组件中，你可以通过 useAuth hook 访问用户信息：
 */

import { useAuth } from '@/hooks/use-auth'

export function UserInfoExample() {
  // 获取全局用户状态
  const { user, isAuthenticated, updateUser } = useAuth()

  if (!isAuthenticated) {
    return <div>请先登录</div>
  }

  if (!user) {
    return <div>加载用户信息中...</div>
  }

  return (
    <div>
      <h2>用户信息</h2>
      <p>ID: {user.id}</p>
      <p>邮箱: {user.email}</p>
      <p>用户名: {user.username || user.name || '未设置'}</p>
      <p>姓名: {user.name || '未设置'}</p>
      {user.avatar && <img src={user.avatar} alt="头像" />}
      
      {/* 更新用户信息的示例 */}
      <button
        onClick={() => {
          updateUser({ name: '新名称' })
        }}
      >
        更新用户信息
      </button>
    </div>
  )
}

/**
 * 使用示例：
 * 
 * 1. 在任何组件中导入 useAuth：
 *    import { useAuth } from '@/hooks/use-auth'
 * 
 * 2. 在组件中使用：
 *    const { user, isAuthenticated, token, updateUser } = useAuth()
 * 
 * 3. 访问用户信息：
 *    - user?.email - 用户邮箱
 *    - user?.name - 用户姓名
 *    - user?.username - 用户名
 *    - user?.avatar - 头像URL
 *    - user?.id - 用户ID
 * 
 * 4. 更新用户信息：
 *    updateUser({ name: '新名称', avatar: '新头像URL' })
 * 
 * 5. 检查登录状态：
 *    if (isAuthenticated) { ... }
 */

