import { ref } from 'vue'

const currentUser = ref(null)
let loginGeneration = 0

export function accessToken() {
  return currentUser.value?.token || null
}

export function clearAuthSession() {
  loginGeneration += 1
  currentUser.value = null
  for (let i = localStorage.length - 1; i >= 0; i -= 1) {
    const key = localStorage.key(i)
    if (key?.startsWith('factoryflow_demo_cache_')) localStorage.removeItem(key)
  }
}

const loginRole   = ref('biuro')
const loginPin    = ref('')
const loginError  = ref(false)

export function useAuth() {
  async function login() {
    const generation = ++loginGeneration
    const role = loginRole.value
    const pin = loginPin.value
    loginError.value = false
    try {
      const res = await fetch('/api/auth/login', {
        method:  'POST',
        headers: { 'Content-Type': 'application/json' },
        body:    JSON.stringify({ role, pin }),
      })
      if (generation !== loginGeneration) return
      if (!res.ok) { loginError.value = true; return }
      const data = await res.json()
      if (generation !== loginGeneration) return
      const user = { id: data.id, role: data.role, name: data.name, default_shift: data.default_shift, token: data.access_token }
      currentUser.value = user
      loginPin.value = ''
    } catch {
      if (generation === loginGeneration) loginError.value = true
    }
  }

  function logout() {
    clearAuthSession()
    loginPin.value = ''
  }

  return { currentUser, loginRole, loginPin, loginError, login, logout }
}
