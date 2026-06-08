import { ref } from 'vue'
import { api } from './useApi'

const pytania     = ref([])
const answerDraft = ref({})
const pendingCount = ref(0)

export function useNotifications() {
  async function loadPytania() {
    pytania.value    = await api('/params')
    pendingCount.value = pytania.value.filter(p => p.status === 'pending').length
  }

  async function submitAnswer(paramId) {
    const text = answerDraft.value[paramId]
    if (!text) return
    await api(`/params/${paramId}/answer`, { method: 'PATCH', body: { answer_text: text } })
    answerDraft.value[paramId] = ''
    await loadPytania()
  }

  return { pytania, answerDraft, pendingCount, loadPytania, submitAnswer }
}
