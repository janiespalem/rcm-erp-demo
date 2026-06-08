import { useToast } from './useToast'

const BASE = `${location.origin}/api`

function getToken() {
  try {
    const stored = localStorage.getItem('rcm_user')
    return stored ? JSON.parse(stored).token : null
  } catch {
    return null
  }
}

export async function openPdf(path) {
  const token = getToken()
  const headers = {}
  if (token) headers['Authorization'] = `Bearer ${token}`
  const res = await fetch(`${location.origin}/api` + path, { headers })
  if (!res.ok) {
    const { show } = useToast()
    show(`Błąd ${res.status}: nie udało się pobrać PDF`)
    return
  }
  const blob = await res.blob()
  const url = URL.createObjectURL(blob)
  window.open(url, '_blank')
  setTimeout(() => URL.revokeObjectURL(url), 60000)
}

export async function api(path, opts = {}) {
  const token = getToken()
  const headers = { 'Content-Type': 'application/json' }
  if (token) headers['Authorization'] = `Bearer ${token}`

  try {
    const res = await fetch(BASE + path, {
      headers,
      ...opts,
      body: opts.body ? JSON.stringify(opts.body) : undefined,
    })
    if (!res.ok) {
      const raw = await res.text().catch(() => '')
      let msg = `Błąd ${res.status}`
      try { const e = JSON.parse(raw); msg = e.detail || JSON.stringify(e) }
      catch { msg = raw || msg }
      if (res.status === 401) {
        localStorage.removeItem('rcm_user')
        location.reload()
        return
      }
      throw new Error(msg)
    }
    if (res.status === 204) return null
    return res.json()
  } catch (err) {
    const { show } = useToast()
    show(err.message || 'Błąd połączenia z serwerem')
    throw err
  }
}
