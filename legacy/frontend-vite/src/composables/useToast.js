import { ref } from 'vue'

const toasts = ref([])
let _nextId = 0

export function useToast() {
  function show(message, type = 'error') {
    const id = ++_nextId
    toasts.value.push({ id, message, type })
    setTimeout(() => { toasts.value = toasts.value.filter(t => t.id !== id) }, 4000)
  }

  return { toasts, show }
}
