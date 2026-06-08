import { ref } from 'vue'

const pending = ref(null) // { message, resolve }

export function useConfirm() {
  function confirm(message, { confirmLabel = 'Usuń' } = {}) {
    return new Promise(resolve => {
      pending.value = { message, resolve, confirmLabel }
    })
  }

  function respond(result) {
    if (pending.value) {
      pending.value.resolve(result)
      pending.value = null
    }
  }

  return { pending, confirm, respond }
}
