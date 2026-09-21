import { useToast } from './useToast'
import { accessToken, clearAuthSession } from './useAuth'

const BASE = `${location.origin}/api`

function responseFilename(res, path) {
  const disposition = res.headers.get('content-disposition') || ''
  const match = disposition.match(/filename="?([^";]+)"?/i)
  return (match?.[1] || path.split('/').pop() || 'plik').replace(/[\\/]/g, '_')
}

function downloadBlob(blob, filename) {
  const url = URL.createObjectURL(blob)
  const link = document.createElement('a')
  link.href = url
  link.download = filename
  link.click()
  setTimeout(() => URL.revokeObjectURL(url), 0)
}

export async function openPdf(path) {
  const preview = window.open('about:blank', '_blank')
  if (preview) preview.opener = null

  const token = accessToken()
  const headers = {}
  if (token) headers['Authorization'] = `Bearer ${token}`
  try {
    const res = await fetch(`${location.origin}/api` + path, { headers })
    if (!res.ok) {
      preview?.close()
      if (res.status === 401) {
        clearAuthSession()
        location.reload()
        return
      }
      const { show } = useToast()
      show(`Błąd ${res.status}: nie udało się pobrać pliku`)
      return
    }

    const blob = await res.blob()
    const contentType = (res.headers.get('content-type') || '').split(';')[0]
    if (contentType !== 'application/pdf') {
      preview?.close()
      downloadBlob(blob, responseFilename(res, path))
      return
    }

    if (preview) {
      const url = URL.createObjectURL(new Blob([blob], { type: 'application/pdf' }))
      preview.location.replace(url)
      setTimeout(() => URL.revokeObjectURL(url), 60000)
    } else {
      downloadBlob(blob, responseFilename(res, path))
    }
  } catch {
    preview?.close()
    const { show } = useToast()
    show('Nie udało się pobrać pliku')
  }
}

// --- Lekki cache localStorage (stale-while-revalidate) dla wolno zmiennych list.
// Ref hydratuje się z cache synchronicznie (natychmiastowy render), a revalidacja
// leci w tle. Bez zależności — po prostu JSON w localStorage.
export function readCache(key) {
  try { return JSON.parse(localStorage.getItem('factoryflow_demo_cache_' + key)) } catch { return null }
}
export function writeCache(key, data) {
  try { localStorage.setItem('factoryflow_demo_cache_' + key, JSON.stringify(data)) } catch { /* quota/full — ignoruj */ }
}

export async function api(path, opts = {}) {
  const token = accessToken()
  const isFormData = typeof FormData !== 'undefined' && opts.body instanceof FormData
  const headers = isFormData ? {} : { 'Content-Type': 'application/json' }
  if (token) headers['Authorization'] = `Bearer ${token}`
  const { body, headers: extraHeaders, preserveAuth = false, ...requestOptions } = opts

  try {
    const res = await fetch(BASE + path, {
      ...requestOptions,
      headers: { ...headers, ...extraHeaders },
      body: body ? (isFormData ? body : JSON.stringify(body)) : undefined,
    })
    if (!res.ok) {
      const raw = await res.text().catch(() => '')
      let msg = `Błąd ${res.status}`
      try { const e = JSON.parse(raw); msg = e.detail || JSON.stringify(e) }
      catch { msg = raw || msg }
      if (res.status === 401 && !preserveAuth) {
        clearAuthSession()
        location.reload()
        throw new Error('Sesja wygasła')
      }
      const error = new Error(msg)
      error.status = res.status
      try { error.detail = JSON.parse(raw).detail } catch { /* text response */ }
      throw error
    }
    if (res.status === 204) return null
    return res.json()
  } catch (err) {
    // opts.quiet — dla oczekiwanych błędów (np. brak wyceny = 404), bez toasta.
    if (!opts.quiet) {
      const { show } = useToast()
      show(err.message || 'Błąd połączenia z serwerem')
    }
    throw err
  }
}
