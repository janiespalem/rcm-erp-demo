<script setup>
import { ref, computed, onMounted, watch } from 'vue'
import { api, openPdf } from '@/composables/useApi'
import { useAuth } from '@/composables/useAuth'
import { useToast } from '@/composables/useToast'
import { useConfirm } from '@/composables/useConfirm'
import { STATUS_PL } from '@/utils/format'
import { stageForStatus } from '@/utils/orderWorkflow'

const props = defineProps({
  order: Object,
})

const { currentUser } = useAuth()
const { show } = useToast()
const { confirm } = useConfirm()

const order = computed(() => props.order || {})
const role = computed(() => currentUser.value?.role)
const events = ref([])
const attachments = ref([])
const params = ref([])
const uploading = ref(false)
const questionDraft = ref('')
const railTab = ref('dokumenty')

const API_BASE = `${location.origin}/api`
const MAX_UPLOAD_BYTES = 100 * 1024 * 1024

const PARAM_QUESTIONS = [
  'Proszę podać markę stali (np. S235, S355, Hardox)',
  'Proszę podać grubość ścianki / blachy (mm)',
  'Proszę potwierdzić wymiary zewnętrzne (dł × szer × wys)',
  'Czy klient dostarczy rysunek / rysunek CAD?',
  'Jaka jest wymagana klasa spawania?',
]

const EVENT_LABELS = {
  created: 'Zlecenie utworzone',
  triage: 'Triage - wynik',
  quoted: 'Wycena zapisana',
  confirmed: 'Wycena zatwierdzona',
  started: 'Produkcja rozpoczęta',
  completed: 'Produkcja zakończona',
  delivered: 'Wydano klientowi',
}

const pendingParams = computed(() => params.value.filter(p => p.status !== 'answered').length)

function fmtDateTime(iso) {
  if (!iso) return ''
  const d = new Date(iso)
  return d.toLocaleDateString('pl-PL', { day: '2-digit', month: '2-digit', year: 'numeric' })
    + ' ' + d.toLocaleTimeString('pl-PL', { hour: '2-digit', minute: '2-digit' })
}

// Open the rail to the section most relevant to the current stage: drawings/files
// while the order is still new, documents (oferta/arkusz) once it is moving.
function defaultRailTab() {
  return stageForStatus(order.value.status) === 'nowe' ? 'pliki' : 'dokumenty'
}

async function loadEvents() {
  if (!order.value.id) {
    events.value = []
    return
  }
  try {
    events.value = await api(`/orders/${order.value.id}/events`)
  } catch {
    events.value = []
  }
}

async function loadAttachments() {
  if (!order.value.id) {
    attachments.value = []
    return
  }
  try {
    attachments.value = await api(`/orders/${order.value.id}/attachments`)
  } catch {
    attachments.value = []
  }
}

async function loadParams() {
  if (!order.value.id) {
    params.value = []
    return
  }
  try {
    params.value = await api(`/orders/${order.value.id}/params`)
  } catch {
    params.value = []
  }
}

async function loadRailData() {
  await Promise.all([loadEvents(), loadAttachments(), loadParams()])
}

async function reload() {
  await loadEvents()
}

async function uploadAttachment(event) {
  if (uploading.value) return
  const file = event.target.files?.[0]
  if (!file) return
  if (file.size > MAX_UPLOAD_BYTES) {
    show('Plik za duży (max 100 MB)')
    event.target.value = ''
    return
  }

  uploading.value = true
  try {
    const fd = new FormData()
    fd.append('file', file)
    const stored = localStorage.getItem('rcm_user')
    const token = stored ? JSON.parse(stored).token : null
    const headers = token ? { Authorization: `Bearer ${token}` } : {}
    const res = await fetch(`${API_BASE}/orders/${order.value.id}/attachments`, { method: 'POST', body: fd, headers })
    if (!res.ok) {
      show('Błąd uploadu pliku')
      return
    }
    await loadAttachments()
  } finally {
    uploading.value = false
    event.target.value = ''
  }
}

async function deleteAttachment(att) {
  if (!await confirm(`Usunąć plik "${att.filename}"?`)) return
  await api(`/attachments/${att.id}`, { method: 'DELETE' })
  await loadAttachments()
}

async function openAttachment(att) {
  await openPdf(`/attachments/${att.id}/download`)
}

async function submitQuestion() {
  const question = questionDraft.value.trim()
  if (!question) return
  await api(`/orders/${order.value.id}/params`, {
    method: 'POST',
    body: { question_text: question },
  })
  questionDraft.value = ''
  await loadParams()
  show('Pytanie wysłane do Biuro')
}

watch(() => props.order?.id, async () => {
  railTab.value = defaultRailTab()
  questionDraft.value = ''
  await loadRailData()
})

onMounted(async () => {
  railTab.value = defaultRailTab()
  await loadRailData()
})

defineExpose({ reload })
</script>

<template>
  <aside class="workspace-rail">
    <nav class="rail-tabs" aria-label="Sekcje zlecenia">
      <button type="button" :class="{ active: railTab === 'dokumenty' }" @click="railTab = 'dokumenty'">Dokumenty</button>
      <button type="button" :class="{ active: railTab === 'pliki' }" @click="railTab = 'pliki'">
        Pliki<span v-if="attachments.length" class="rail-count">{{ attachments.length }}</span>
      </button>
      <button type="button" :class="{ active: railTab === 'pytania' }" @click="railTab = 'pytania'">
        Pytania<span v-if="pendingParams" class="rail-count rail-count-warn">{{ pendingParams }}</span>
      </button>
      <button type="button" :class="{ active: railTab === 'historia' }" @click="railTab = 'historia'">Historia</button>
    </nav>

    <section v-if="railTab === 'dokumenty'" class="rail-panel">
      <button type="button" @click="openPdf('/orders/' + order.id + '/oferta')">Oferta dla klienta</button>
      <button type="button" @click="openPdf('/orders/' + order.id + '/pdf')">Arkusz produkcyjny</button>
      <button type="button" @click="openPdf('/orders/' + order.id + '/pdf/split')">Arkusze operacji ZIP</button>
    </section>

    <section v-if="railTab === 'pliki'" class="rail-panel">
      <div v-if="attachments.length" class="file-list">
        <div v-for="att in attachments" :key="att.id" class="file-row">
          <a href="#" @click.prevent="openAttachment(att)">{{ att.filename }}</a>
          <small>
            {{ att.size_bytes ? Math.round(att.size_bytes / 1024) + ' KB' : 'plik' }}
            <template v-if="att.uploaded_by"> · {{ att.uploaded_by }}</template>
          </small>
          <button type="button" class="file-delete" @click="deleteAttachment(att)">Usuń</button>
        </div>
      </div>
      <p v-else class="empty-rail">Brak plików</p>
      <label class="upload-control">
        <span>{{ uploading ? 'Wysyłanie...' : 'Dodaj plik' }}</span>
        <input
          type="file"
          :disabled="uploading"
          accept=".pdf,.dxf,.dwg,.jpg,.jpeg,.png,.xlsx,.docx"
          @change="uploadAttachment"
        >
      </label>
    </section>

    <section v-if="railTab === 'pytania'" class="rail-panel">
      <div v-if="params.length" class="param-list">
        <div
          v-for="param in params"
          :key="param.id"
          class="param-row"
          :class="param.status === 'answered' ? 'answered' : 'pending'"
        >
          <div class="param-meta">
            {{ param.asked_at?.slice(0, 16).replace('T', ' ') || '—' }}
            <span>{{ param.status === 'answered' ? 'Odpowiedziano' : 'Oczekuje' }}</span>
          </div>
          <strong>{{ param.question_text }}</strong>
          <p v-if="param.answer_text">{{ param.answer_text }}</p>
          <p v-else>Biuro jeszcze nie odpowiedziało.</p>
        </div>
      </div>
      <p v-else class="empty-rail">Brak pytań</p>
      <div v-if="['technolog', 'dyrektor_produkcji'].includes(role)" class="question-box">
        <div class="question-presets">
          <button
            v-for="question in PARAM_QUESTIONS"
            :key="question"
            type="button"
            @click="questionDraft = question"
          >
            {{ question }}
          </button>
        </div>
        <textarea v-model="questionDraft" rows="3" placeholder="Pytanie do Biuro"></textarea>
        <button type="button" :disabled="!questionDraft.trim()" @click="submitQuestion">Wyślij pytanie</button>
      </div>
    </section>

    <section v-if="railTab === 'historia'" class="rail-panel">
      <div v-if="events.length" class="event-list">
        <div v-for="event in events" :key="event.id" class="event-row">
          <span class="event-dot"></span>
          <div>
            <strong>{{ EVENT_LABELS[event.event_type] || event.event_type }}</strong>
            <p v-if="event.old_status || event.new_status">
              <span v-if="event.old_status">{{ STATUS_PL[event.old_status] || event.old_status }}</span>
              <span v-if="event.old_status && event.new_status"> → </span>
              <span v-if="event.new_status">{{ STATUS_PL[event.new_status] || event.new_status }}</span>
            </p>
            <small>{{ [event.user_name, event.note, fmtDateTime(event.created_at)].filter(Boolean).join(' · ') }}</small>
          </div>
        </div>
      </div>
      <p v-else class="empty-rail">Brak historii</p>
    </section>
  </aside>
</template>

<style scoped>
.workspace-rail {
  display: grid;
  gap: 14px;
  align-content: start;
}

.rail-tabs {
  display: flex;
  gap: 4px;
  border: 1px solid var(--border);
  border-radius: 8px;
  background: var(--sl-50);
  padding: 4px;
}

.rail-tabs button {
  flex: 1;
  border: none;
  border-radius: 6px;
  background: transparent;
  color: var(--muted);
  padding: 7px 6px;
  cursor: pointer;
  font-size: 0.78rem;
  font-weight: 800;
  display: inline-flex;
  align-items: center;
  justify-content: center;
  gap: 5px;
}

.rail-tabs button:hover {
  color: var(--sl-800);
}

.rail-tabs button.active {
  background: #fff;
  color: var(--rcm-blue);
  box-shadow: var(--sh-sm);
}

.rail-count {
  min-width: 17px;
  border-radius: 999px;
  background: var(--sl-200);
  color: var(--sl-700);
  padding: 1px 5px;
  font-family: var(--font-data);
  font-size: 0.68rem;
}

.rail-count-warn {
  background: rgba(245, 158, 11, 0.18);
  color: var(--rcm-warn);
}

.rail-panel {
  border: 1px solid var(--border);
  border-radius: 8px;
  background: #fff;
  padding: 15px;
  display: grid;
  gap: 8px;
}

.rail-panel > button,
.question-box > button {
  width: 100%;
  border: 1px solid var(--border);
  border-radius: 7px;
  background: var(--sl-50);
  color: var(--sl-800);
  padding: 9px 10px;
  text-align: left;
  cursor: pointer;
  font-weight: 750;
}

.rail-panel > button:hover,
.question-box > button:hover {
  border-color: var(--rcm-accent);
  background: #fff;
}

.file-list,
.param-list {
  display: grid;
  gap: 8px;
}

.file-row {
  border: 1px solid var(--border-soft);
  border-radius: 7px;
  background: var(--sl-50);
  padding: 8px;
  display: grid;
  gap: 3px;
}

.file-row a {
  color: var(--rcm-blue);
  font-size: 0.84rem;
  font-weight: 850;
  overflow-wrap: anywhere;
}

.file-row small {
  color: var(--muted);
  font-size: 0.72rem;
}

.file-delete {
  width: fit-content;
  border: 1px solid var(--rcm-red);
  border-radius: 6px;
  background: #fff;
  color: var(--rcm-red);
  padding: 4px 8px;
  cursor: pointer;
  font-size: 0.72rem;
  font-weight: 800;
}

.upload-control {
  border: 1px dashed var(--border);
  border-radius: 7px;
  background: var(--sl-50);
  color: var(--sl-700);
  padding: 10px;
  cursor: pointer;
  font-size: 0.82rem;
  font-weight: 850;
  text-align: center;
}

.upload-control input {
  display: none;
}

.param-row {
  border-left: 3px solid var(--rcm-warn);
  border-radius: 0 7px 7px 0;
  background: var(--sl-50);
  padding: 8px 10px;
  display: grid;
  gap: 4px;
}

.param-row.answered {
  border-left-color: var(--rcm-green);
}

.param-meta {
  color: var(--muted);
  font-size: 0.72rem;
  display: flex;
  justify-content: space-between;
  gap: 8px;
}

.param-meta span {
  color: var(--sl-700);
  font-weight: 850;
}

.param-row strong {
  color: var(--text);
  font-size: 0.82rem;
  line-height: 1.35;
}

.param-row p {
  margin: 0;
  color: var(--muted);
  font-size: 0.78rem;
  line-height: 1.4;
}

.question-box {
  display: grid;
  gap: 8px;
}

.question-presets {
  display: flex;
  flex-wrap: wrap;
  gap: 5px;
}

.question-presets button {
  border: 1px solid var(--border);
  border-radius: 999px;
  background: #fff;
  color: var(--sl-700);
  padding: 4px 8px;
  cursor: pointer;
  font-size: 0.68rem;
  font-weight: 800;
}

.question-presets button:hover {
  border-color: var(--rcm-accent);
}

.question-box textarea {
  width: 100%;
  border: 1px solid var(--border);
  border-radius: 7px;
  padding: 8px 9px;
  color: var(--text);
  font: inherit;
  font-size: 0.82rem;
  resize: vertical;
}

.event-list {
  display: grid;
  gap: 10px;
}

.event-row {
  display: grid;
  grid-template-columns: 10px minmax(0, 1fr);
  gap: 9px;
}

.event-dot {
  width: 9px;
  height: 9px;
  border-radius: 50%;
  background: var(--rcm-accent);
  margin-top: 5px;
}

.event-row strong {
  display: block;
  color: var(--sl-800);
  font-size: 0.82rem;
}

.event-row p,
.event-row small,
.empty-rail {
  margin: 2px 0 0;
  color: var(--muted);
  font-size: 0.76rem;
}
</style>
