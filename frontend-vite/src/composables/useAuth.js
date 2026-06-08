import { ref } from 'vue'

const _stored     = localStorage.getItem('rcm_user')
const currentUser = ref(_stored ? JSON.parse(_stored) : null)
const loginRole   = ref('biuro')
const loginPin    = ref('')
const loginError  = ref(false)

export function useAuth() {
  async function login() {
    loginError.value = false
    try {
      const res = await fetch('/api/auth/login', {
        method:  'POST',
        headers: { 'Content-Type': 'application/json' },
        body:    JSON.stringify({ role: loginRole.value, pin: loginPin.value }),
      })
      if (!res.ok) { loginError.value = true; return }
      const data = await res.json()
      const user = { role: data.role, name: data.name, token: data.access_token }
      currentUser.value = user
      localStorage.setItem('rcm_user', JSON.stringify(user))
    } catch {
      loginError.value = true
    }
  }

  function logout() {
    currentUser.value = null
    loginPin.value    = ''
    localStorage.removeItem('rcm_user')
  }

  return { currentUser, loginRole, loginPin, loginError, login, logout }
}
