import { ref } from 'vue'
import { api } from './useApi'

const pytania     = ref([])
const answerDraft = ref({})
const pendingCount = ref(0)
const submittingAnswers = ref(new Set())

export function useNotifications() {
  async function loadPytania() {
    pytania.value    = await api('/params')
    pendingCount.value = pytania.value.filter(p => p.status === 'pending').length
  }

  async function submitAnswer(paramId) {
    const text = answerDraft.value[paramId]?.trim()
    if (!text || submittingAnswers.value.has(paramId)) return
    submittingAnswers.value.add(paramId)
    try {
      await api(`/params/${paramId}/answer`, { method: 'PATCH', body: { answer_text: text } })
      answerDraft.value[paramId] = ''
      await loadPytania()
    } finally {
      submittingAnswers.value.delete(paramId)
    }
  }

  return { pytania, answerDraft, pendingCount, submittingAnswers, loadPytania, submitAnswer }
}
