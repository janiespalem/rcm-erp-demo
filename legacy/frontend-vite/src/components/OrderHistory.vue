<script setup>
import { ref, computed, watch, onMounted } from 'vue'
import { api, readCache, writeCache } from '@/composables/useApi'
import { STATUS_PL } from '@/utils/format'

// Historia zlecenia — dostępna dla KAŻDEGO zlecenia, na dowolnym etapie, dla obu
// ról. Otwierana jednym kliknięciem (z listy zleceń i z panelu wyceny); nie jest
// zaszyta w podzakładce karty. Pobiera /orders/{id}/events i pokazuje oś czasu.
const props = defineProps({
  order: { type: Object, required: true },
})
const emit = defineEmits(['close'])

const events = ref([])
const loading = ref(true)
const failed = ref(false)

const title = computed(() => props.order?.order_number || `#${props.order?.id}`)

const EVENT_LABELS = {
  created: 'Zlecenie utworzone',
  triage: 'Triage — wynik',
  quoted: 'Wycena zapisana',
  confirmed: 'Wycena zatwierdzona',
  started: 'Produkcja rozpoczęta',
  completed: 'Produkcja zakończona',
  delivered: 'Wydano klientowi',
  archived: 'Przeniesiono do archiwum',
  restored: 'Przywrócono z archiwum',
  edited: 'Edytowano',
  quote_saved: 'Zapisano wycenę',
  file_added: 'Dodano plik',
  file_removed: 'Usunięto plik',
  question_asked: 'Zadano pytanie',
  question_answered: 'Odpowiedziano',
}

function statusLabel(s) { return STATUS_PL[s] || s }

function eventTransition(event) {
  const from = event.old_status ? statusLabel(event.old_status) : ''
  const to = event.new_status ? statusLabel(event.new_status) : ''
  if (!from && !to) return ''
  if (from && to) return from === to ? '' : `${from} → ${to}`
  return from || to
}

function fmtActor(event) {
  if (event.user_name && event.user_role) return `${event.user_role} ${event.user_name}`
  return event.user_name || event.user_role || ''
}

function fmtDateTime(iso) {
  if (!iso) return ''
  const d = new Date(iso)
  return d.toLocaleDateString('pl-PL', { day: '2-digit', month: '2-digit', year: 'numeric' })
    + ' ' + d.toLocaleTimeString('pl-PL', { hour: '2-digit', minute: '2-digit' })
}

async function load() {
  failed.value = false
  // Cache-first: pokaż ostatnią znaną historię natychmiast, świeżą dograj w tle.
  const key = 'events_' + props.order.id
  const cached = readCache(key)
  if (cached) { events.value = cached; loading.value = false }
  else { loading.value = true }
  try {
    const fresh = await api(`/orders/${props.order.id}/events`)
    events.value = fresh
    writeCache(key, fresh)
  } catch {
    if (!cached) { failed.value = true; events.value = [] }
  } finally {
    loading.value = false
  }
}

watch(() => props.order?.id, load)
onMounted(load)
</script>

<template>
  <div class="hist-overlay" @click.self="emit('close')">
    <section class="hist-modal" role="dialog" aria-label="Historia zlecenia">
      <header class="hist-head">
        <div>
          <h3>Historia {{ title }}</h3>
          <p>{{ order?.client || 'Bez klienta' }} · {{ STATUS_PL[order?.status] || order?.status }}</p>
        </div>
        <button type="button" class="hist-close" @click="emit('close')">✕</button>
      </header>

      <div class="hist-body">
        <p v-if="loading" class="hist-msg">Ładowanie…</p>
        <p v-else-if="failed" class="hist-msg">Nie udało się wczytać historii.</p>
        <p v-else-if="!events.length" class="hist-msg">Brak historii dla tego zlecenia.</p>
        <div v-else class="event-list">
          <div v-for="event in events" :key="event.id" class="event-row">
            <span class="event-dot"></span>
            <div>
              <strong>{{ EVENT_LABELS[event.event_type] || event.event_type }}</strong>
              <p v-if="eventTransition(event)" class="event-trans">{{ eventTransition(event) }}</p>
              <small>{{ [fmtActor(event), event.note, fmtDateTime(event.created_at)].filter(Boolean).join(' · ') }}</small>
            </div>
          </div>
        </div>
      </div>
    </section>
  </div>
</template>

<style scoped>
.hist-overlay {
  position: fixed;
  inset: 0;
  background: rgba(0, 0, 0, 0.45);
  display: flex;
  align-items: center;
  justify-content: center;
  z-index: 250;
  padding: 24px;
}
.hist-modal {
  background: var(--card);
  border-radius: var(--radius);
  border: 1px solid var(--border);
  width: 100%;
  max-width: 540px;
  max-height: 80vh;
  display: flex;
  flex-direction: column;
  box-shadow: 0 12px 40px rgba(0, 0, 0, 0.25);
}
.hist-head {
  display: flex;
  justify-content: space-between;
  align-items: flex-start;
  gap: 12px;
  padding: 16px 20px;
  border-bottom: 1px solid var(--border);
}
.hist-head h3 { margin: 0; color: var(--rcm-blue); font-size: 1.05rem; font-weight: 850; }
.hist-head p  { margin: 3px 0 0; color: var(--muted); font-size: 0.84rem; }
.hist-close {
  border: 1px solid var(--border);
  border-radius: 999px;
  background: var(--card);
  color: var(--sl-700);
  width: 30px; height: 30px;
  cursor: pointer;
  font-size: 0.9rem;
  flex-shrink: 0;
}
.hist-body { overflow-y: auto; padding: 16px 20px; }
.hist-msg { color: var(--muted); font-size: 0.9rem; padding: 16px 0; text-align: center; }

.event-list { display: grid; gap: 14px; }
.event-row { display: grid; grid-template-columns: 14px 1fr; gap: 10px; }
.event-dot {
  width: 9px; height: 9px; border-radius: 999px;
  background: var(--rcm-blue); margin-top: 5px;
  box-shadow: 0 0 0 3px rgba(26, 58, 92, 0.12);
}
.event-row strong { font-size: 0.9rem; color: var(--text); }
.event-trans { margin: 2px 0 0; font-size: 0.82rem; color: var(--sl-700); font-weight: 700; }
.event-row small { display: block; margin-top: 2px; color: var(--muted); font-size: 0.78rem; }
</style>
