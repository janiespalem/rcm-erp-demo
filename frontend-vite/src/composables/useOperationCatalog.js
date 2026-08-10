import { ref } from 'vue'
import { api, readCache, writeCache } from './useApi'

// Katalog operacji zmienia się rzadko — trzymamy go raz w pamięci (+ cache),
// żeby panel wyceny nie pobierał go przy każdym otwarciu.
const operationCatalog = ref(readCache('opcatalog') || [])
let loaded = false

export function useOperationCatalog() {
  async function loadOperationCatalog(force = false) {
    if (loaded && !force && operationCatalog.value.length) return operationCatalog.value
    const fresh = await api('/operation-catalog/')
    operationCatalog.value = fresh
    writeCache('opcatalog', fresh)
    loaded = true
    return fresh
  }
  return { operationCatalog, loadOperationCatalog }
}
