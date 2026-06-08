<script setup>
import { ref, computed, watch } from 'vue'
import { api } from '@/composables/useApi'
import { useAuth } from '@/composables/useAuth'
import { useOrders } from '@/composables/useOrders'
import { useApprovedMaterials } from '@/composables/useApprovedMaterials'
import { moneyLabel, STATUS_PL } from '@/utils/format'
import {
  isOrderOverdue,
  canQuote as wfCanQuote,
  canConfirm as wfCanConfirm,
  canStartProduction,
  canCompleteProduction,
  canDeliver as wfCanDeliver,
} from '@/utils/orderWorkflow'
import QuotePanel from '@/components/QuotePanel.vue'
import OrderRail from '@/components/OrderRail.vue'

const props = defineProps({
  order: Object,
  showClose: { type: Boolean, default: false },
})
const emit = defineEmits(['close', 'saved'])

const { currentUser } = useAuth()
const {
  loadOrders,
  deleteOrder: doDelete,
  confirmOrder,
  startOrder,
  completeOrder,
  deliverOrder,
} = useOrders()
const { approvedMaterials } = useApprovedMaterials()

const order = ref({ ...props.order })
const editMode = ref(false)
const confirmDel = ref(false)
const deleteError = ref('')
const form = ref({})
const busyAction = ref('')
const quoteOpen = ref(false)
const rail = ref(null)

function fmtDeadline(value) {
  return value ? value.split('-').reverse().join('.') : '—'
}

function orderTypeLabel(value) {
  switch (value) {
    case 'remont': return 'Remont / naprawa'
    case 'catalog': return 'Produkt z katalogu'
    case 'nowa_czesc': return 'Nowa część / projekt'
    case 'zbrojenie': return 'Zbrojenie'
    default: return '—'
  }
}

const role = computed(() => currentUser.value?.role)
const canConfirm = computed(() => wfCanConfirm(order.value, role.value))
const canStart = computed(() => canStartProduction(order.value, role.value))
const canComplete = computed(() => canCompleteProduction(order.value, role.value))
const canDeliver = computed(() => wfCanDeliver(order.value, role.value))
// Quoting is restricted to technolog/dyrektor on the backend (_TECH); gate the UI
// to match so biuro never sees a quote action that would 403.
const canQuote = computed(() =>
  wfCanQuote(order.value) && ['technolog', 'dyrektor_produkcji'].includes(role.value)
)
const canEditQuote = computed(() => order.value.status === 'quoted' && canQuote.value)
const overdue = computed(() => isOrderOverdue(order.value))

const nextAction = computed(() => {
  if (['draft', 'triage'].includes(order.value.status)) {
    return {
      title: 'Zlecenie czeka na klasyfikację',
      body: 'Po triage trafi do wyceny albo zostanie oznaczone jako standard/odrzut.',
      label: '',
      enabled: false,
    }
  }
  if (order.value.status === 'standard') {
    return {
      title: 'Zlecenie standardowe',
      body: 'Ten status nie korzysta z ręcznej wyceny w prostym pipeline.',
      label: '',
      enabled: false,
    }
  }
  if (order.value.status === 'niestandard') {
    return {
      title: 'Zlecenie czeka na wycenę',
      body: 'Otwórz panel wyceny i zapisz kwotę dla biura.',
      label: 'Wyceń zlecenie',
      enabled: canQuote.value,
      waitingFor: 'Technologa',
      handler: () => { quoteOpen.value = true },
    }
  }
  if (order.value.status === 'quoted') {
    return {
      title: 'Wycena gotowa do decyzji',
      body: 'Po zatwierdzeniu zlecenie trafi do produkcji.',
      label: 'Zatwierdź i wyślij do produkcji',
      enabled: canConfirm.value,
      waitingFor: 'Biuro',
      handler: () => runStatusAction('confirm', confirmOrder, 'in_production'),
    }
  }
  if (order.value.status === 'in_production') {
    return {
      title: 'Produkcja czeka na start',
      body: 'Rozpocznij pracę, gdy zlecenie faktycznie trafia na warsztat.',
      label: 'Rozpocznij produkcję',
      enabled: canStart.value,
      waitingFor: 'Technologa',
      handler: () => runStatusAction('start', startOrder, 'w_trakcie'),
    }
  }
  if (order.value.status === 'w_trakcie') {
    return {
      title: 'Zlecenie jest w produkcji',
      body: 'Po zakończeniu oznacz je jako gotowe do wydania.',
      label: 'Oznacz jako gotowe',
      enabled: canComplete.value,
      waitingFor: 'Technologa',
      handler: () => runStatusAction('complete', completeOrder, 'gotowe'),
    }
  }
  if (order.value.status === 'gotowe') {
    return {
      title: 'Gotowe do wydania',
      body: 'Zamknij obieg po odbiorze przez klienta.',
      label: 'Wydano klientowi',
      enabled: canDeliver.value,
      waitingFor: 'Biuro',
      handler: () => runStatusAction('deliver', deliverOrder, 'wydane'),
    }
  }
  if (order.value.status === 'wydane') {
    return { title: 'Zlecenie wydane', body: 'Obieg tego zlecenia jest zakończony.', label: '', enabled: false }
  }
  if (['rejected', 'odrzut'].includes(order.value.status)) {
    return { title: 'Zlecenie odrzucone', body: 'Nie wymaga dalszej pracy w pipeline.', label: '', enabled: false }
  }
  return { title: 'Brak następnej akcji', body: 'Status nie ma przypisanej akcji w prostym pipeline.', label: '', enabled: false }
})

const materialNeedsReview = computed(() => {
  const material = order.value.material?.toLowerCase()
  return !!material && approvedMaterials.value.length > 0 &&
    !approvedMaterials.value.some(m => m.name.toLowerCase() === material)
})

watch(() => props.order, (next, previous) => {
  order.value = { ...next }
  editMode.value = false
  confirmDel.value = false
  deleteError.value = ''
  if (next?.id !== previous?.id) {
    quoteOpen.value = false
  }
}, { deep: false })

async function runStatusAction(key, fn, nextStatus) {
  if (busyAction.value) return
  busyAction.value = key
  try {
    await fn(order.value.id)
    order.value = { ...order.value, status: nextStatus }
    await Promise.all([rail.value?.reload?.(), loadOrders()])
  } finally {
    busyAction.value = ''
  }
}

async function handleQuoteSaved() {
  quoteOpen.value = false
  await rail.value?.reload?.()
}

function startEdit() {
  const o = order.value
  form.value = {
    deadline: o.deadline || '',
    material: o.material || '',
    notes: o.notes || '',
    estimated_value: o.estimated_value || '',
    has_drawing: !!o.has_drawing,
    requires_visit: !!o.requires_visit,
  }
  confirmDel.value = false
  deleteError.value = ''
  editMode.value = true
}

function cancelEdit() {
  editMode.value = false
  confirmDel.value = false
  deleteError.value = ''
}

async function saveEdit() {
  const f = form.value
  const payload = {}
  if (f.deadline) payload.deadline = f.deadline
  if (f.material) {
    payload.material = f.material
    if (f.material !== order.value.material) payload.approved_material_id = null
  }
  payload.notes = f.notes || ''
  if (f.estimated_value !== '') payload.estimated_value = parseFloat(f.estimated_value) || 0
  payload.has_drawing = !!f.has_drawing
  payload.requires_visit = !!f.requires_visit

  const updated = await api(`/orders/${order.value.id}`, { method: 'PATCH', body: payload })
  order.value = updated
  editMode.value = false
  await loadOrders()
}

async function confirmDelete() {
  try {
    await doDelete(order.value.id)
    emit('saved')
    emit('close')
  } catch (e) {
    deleteError.value = e.message || 'Błąd usuwania'
    confirmDel.value = false
  }
}
</script>

<template>
    <section class="order-workspace" aria-label="Zlecenie">
      <header class="workspace-header">
        <div class="workspace-title">
          <button v-if="showClose" class="close-link" type="button" @click="emit('close')">Zamknij</button>
          <div>
            <h2>Zlecenie {{ order.order_number || '#' + order.id }}</h2>
            <p>{{ order.client || 'Bez klienta' }}</p>
          </div>
        </div>
        <div class="header-actions">
          <span :class="'badge badge-' + (order.status || 'draft')">{{ STATUS_PL[order.status] || order.status || '—' }}</span>
          <button v-if="!editMode" class="btn btn-outline btn-sm" type="button" @click="startEdit">Edytuj</button>
        </div>
      </header>

      <div v-if="order.is_defence" class="defence-banner">
        Projekt zbrojeniowy / MON - dokumenty poufne
      </div>

      <QuotePanel
        v-if="quoteOpen"
        :order="order"
        :show-close="true"
        class="workspace-quote"
        @close="quoteOpen = false"
        @saved="handleQuoteSaved"
      />

      <div v-else class="workspace-grid">
        <main class="workspace-main">
          <section class="next-action">
            <div>
              <span class="status-line">{{ STATUS_PL[order.status] || order.status || '—' }}</span>
              <h3>{{ nextAction.title }}</h3>
              <p>{{ nextAction.body }}</p>
            </div>
            <button
              v-if="nextAction.label && nextAction.enabled"
              class="btn btn-primary"
              type="button"
              :disabled="!!busyAction"
              @click="nextAction.handler"
            >
              {{ busyAction ? 'Pracuję...' : nextAction.label }}
            </button>
            <span
              v-else-if="nextAction.label && nextAction.waitingFor"
              class="waiting-pill"
            >
              Czeka na {{ nextAction.waitingFor }}
            </span>
          </section>

          <section class="workspace-section">
            <div class="section-head">
              <h3>Dane zlecenia</h3>
              <span v-if="overdue" class="alert-pill">Po terminie</span>
            </div>
            <div class="facts-grid">
              <div>
                <span>Termin</span>
                <strong :class="{ danger: overdue }">{{ fmtDeadline(order.deadline) }}</strong>
              </div>
              <div>
                <span>Typ</span>
                <strong>{{ orderTypeLabel(order.order_type) }}</strong>
              </div>
              <div>
                <span>Ilość</span>
                <strong>{{ order.quantity || 1 }} szt.</strong>
              </div>
              <div>
                <span>Wartość</span>
                <strong>{{ moneyLabel(order.estimated_value) }}</strong>
              </div>
              <div>
                <span>Materiał</span>
                <strong>{{ order.material || '—' }}</strong>
                <small v-if="materialNeedsReview">Niezatwierdzony materiał</small>
              </div>
              <div>
                <span>Flagi</span>
                <strong>{{ order.requires_visit ? 'Wizyta u klienta' : 'Bez wizyty' }}</strong>
                <small v-if="order.has_drawing">Rysunek techniczny</small>
              </div>
            </div>
            <p v-if="order.description" class="description-text">{{ order.description }}</p>
            <p v-if="order.notes" class="notes-text">{{ order.notes }}</p>
            <div v-if="canEditQuote" class="context-actions">
              <button class="inline-action" type="button" @click="quoteOpen = true">Edytuj wycenę</button>
            </div>
            <div v-if="editMode" class="inline-edit">
              <div class="edit-grid">
                <label>
                  <span>Termin</span>
                  <input type="date" v-model="form.deadline">
                </label>
                <label>
                  <span>Materiał</span>
                  <input v-model="form.material" placeholder="np. S235, stal nierdzewna">
                </label>
                <label>
                  <span>Szacowana wartość</span>
                  <input type="number" v-model="form.estimated_value" min="0" step="100">
                </label>
                <label class="wide">
                  <span>Uwagi</span>
                  <textarea v-model="form.notes" rows="3"></textarea>
                </label>
              </div>
              <div class="edit-checks">
                <label><input type="checkbox" v-model="form.has_drawing"> Rysunek techniczny</label>
                <label><input type="checkbox" v-model="form.requires_visit"> Wymaga wizyty</label>
              </div>
              <div class="edit-actions">
                <button class="btn btn-primary" type="button" @click="saveEdit">Zapisz zmiany</button>
                <button class="btn btn-outline" type="button" @click="cancelEdit">Anuluj</button>
              </div>
              <div class="delete-zone">
                <div v-if="deleteError" class="delete-error">Błąd: {{ deleteError }}</div>
                <div v-if="confirmDel" class="confirm-delete">
                  <span>Usunąć zlecenie {{ order.order_number || '#' + order.id }}?</span>
                  <button type="button" @click="confirmDelete">Tak, usuń</button>
                  <button type="button" @click="confirmDel=false">Anuluj</button>
                </div>
                <button v-else class="danger-link" type="button" @click="confirmDel=true; deleteError=''">Usuń zlecenie</button>
              </div>
            </div>
          </section>
        </main>

        <OrderRail ref="rail" :order="order" />
      </div>

      <footer class="workspace-footer">
        <div class="footer-actions">
          <button class="btn btn-outline" type="button" @click="emit('close'); confirmDel=false">Zamknij</button>
        </div>
      </footer>
    </section>
</template>

<style scoped>
.order-workspace {
  width: 100%;
  overflow: auto;
  background: var(--card);
  border: 1px solid var(--border);
  border-radius: 8px;
  box-shadow: var(--sh-lg);
}

.workspace-header {
  position: sticky;
  top: 0;
  z-index: 2;
  display: flex;
  justify-content: space-between;
  align-items: flex-start;
  gap: 16px;
  padding: 18px 22px;
  background: #fff;
  border-bottom: 1px solid var(--border);
}

.workspace-title {
  display: flex;
  gap: 14px;
  align-items: flex-start;
}

.workspace-title h2 {
  margin: 0;
  color: var(--rcm-blue);
  font-size: 1.2rem;
  font-weight: 850;
}

.workspace-title p {
  margin: 3px 0 0;
  color: var(--muted);
  font-size: 0.9rem;
}

.close-link {
  border: 1px solid var(--border);
  border-radius: 999px;
  background: #fff;
  color: var(--sl-700);
  padding: 5px 10px;
  cursor: pointer;
  font-weight: 700;
  font-size: 0.78rem;
}

.header-actions,
.footer-actions {
  display: flex;
  align-items: center;
  gap: 8px;
  flex-wrap: wrap;
  justify-content: flex-end;
}

.defence-banner {
  margin: 16px 22px 0;
  border: 1px solid var(--rcm-red);
  border-radius: 8px;
  background: rgba(220, 38, 38, 0.08);
  color: var(--rcm-red);
  padding: 10px 12px;
  font-size: 0.85rem;
  font-weight: 800;
}

.workspace-grid {
  display: grid;
  grid-template-columns: minmax(0, 1fr) 310px;
  gap: 18px;
  padding: 18px 22px 20px;
}

.workspace-main {
  display: grid;
  gap: 14px;
  align-content: start;
}

.next-action,
.workspace-section {
  border: 1px solid var(--border);
  border-radius: 8px;
  background: #fff;
}

.next-action {
  border-color: var(--rcm-accent);
  background: linear-gradient(90deg, rgba(245, 158, 11, 0.18), #fff 52%);
  padding: 20px;
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 16px;
  box-shadow: var(--sh-sm);
}

.status-line {
  display: inline-flex;
  width: fit-content;
  border-radius: 999px;
  background: rgba(15, 23, 42, 0.08);
  color: var(--sl-700);
  padding: 3px 9px;
  font-size: 0.72rem;
  font-weight: 850;
  margin-bottom: 8px;
}

.next-action h3,
.workspace-section h3 {
  margin: 0;
  color: var(--rcm-blue);
  font-size: 0.95rem;
  font-weight: 850;
}

.next-action p {
  margin: 4px 0 0;
  color: var(--muted);
  font-size: 0.86rem;
}

.next-action .btn {
  min-height: 42px;
  padding-left: 18px;
  padding-right: 18px;
  font-weight: 850;
}

.waiting-pill {
  flex-shrink: 0;
  align-self: center;
  border: 1px dashed var(--border);
  border-radius: 999px;
  background: var(--sl-50);
  color: var(--sl-700);
  padding: 8px 14px;
  font-size: 0.82rem;
  font-weight: 800;
  white-space: nowrap;
}

.workspace-section {
  padding: 15px;
}

.section-head {
  display: flex;
  justify-content: space-between;
  gap: 10px;
  align-items: center;
  margin-bottom: 12px;
}

.alert-pill {
  border-radius: 999px;
  background: rgba(220, 38, 38, 0.1);
  color: var(--rcm-red);
  padding: 3px 8px;
  font-size: 0.72rem;
  font-weight: 800;
}

.facts-grid {
  display: grid;
  grid-template-columns: repeat(3, minmax(0, 1fr));
  gap: 10px;
}

.facts-grid div {
  border: 1px solid var(--border-soft);
  border-radius: 7px;
  background: var(--sl-50);
  padding: 8px 10px;
}

.facts-grid span,
.edit-grid span {
  display: block;
  color: var(--muted);
  font-size: 0.72rem;
  font-weight: 800;
  text-transform: uppercase;
}

.facts-grid strong {
  display: block;
  margin-top: 4px;
  color: var(--text);
  font-size: 0.92rem;
}

.facts-grid small {
  display: block;
  margin-top: 3px;
  color: var(--rcm-warn);
  font-size: 0.72rem;
  font-weight: 700;
}

.facts-grid .danger {
  color: var(--rcm-red);
}

.description-text,
.notes-text {
  color: var(--text);
  font-size: 0.92rem;
  line-height: 1.55;
  white-space: pre-wrap;
}

.notes-text {
  margin-top: 10px;
  border-top: 1px solid var(--border-soft);
  padding-top: 10px;
  color: var(--muted);
}

.workspace-quote {
  margin: 18px 22px 20px;
}

.context-actions {
  border-top: 1px solid var(--border-soft);
  margin-top: 12px;
  padding-top: 10px;
}

.inline-action {
  border: 1px solid var(--border);
  border-radius: 999px;
  background: #fff;
  color: var(--sl-700);
  padding: 6px 10px;
  cursor: pointer;
  font-size: 0.78rem;
  font-weight: 850;
}

.inline-action:hover {
  border-color: var(--rcm-accent);
  color: var(--rcm-blue);
}

.inline-edit {
  border-top: 1px solid var(--border-soft);
  margin-top: 12px;
  padding-top: 12px;
}

.edit-grid {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  gap: 12px;
}

.edit-grid label {
  display: grid;
  gap: 5px;
}

.edit-grid .wide {
  grid-column: 1 / -1;
}

.edit-grid input,
.edit-grid select,
.edit-grid textarea {
  width: 100%;
  border: 1px solid var(--border);
  border-radius: 7px;
  background: #fff;
  color: var(--text);
  padding: 8px 10px;
  font: inherit;
}

.edit-grid textarea {
  resize: vertical;
}

.edit-checks {
  display: flex;
  gap: 16px;
  flex-wrap: wrap;
  margin-top: 14px;
  color: var(--sl-700);
  font-size: 0.86rem;
  font-weight: 700;
}

.edit-checks label {
  display: inline-flex;
  align-items: center;
  gap: 7px;
}

.edit-actions {
  display: flex;
  gap: 8px;
  margin-top: 14px;
}

.delete-zone {
  border-top: 1px solid var(--border-soft);
  margin-top: 14px;
  padding-top: 12px;
  display: grid;
  gap: 8px;
}

.workspace-footer {
  border-top: 1px solid var(--border);
  padding: 14px 22px;
  display: flex;
  align-items: center;
  justify-content: flex-end;
  gap: 14px;
  background: var(--sl-50);
}

.danger-link,
.confirm-delete button,
.delete-error {
  border-radius: 7px;
  font-size: 0.84rem;
}

.danger-link {
  border: 1px solid var(--rcm-red);
  background: #fff;
  color: var(--rcm-red);
  padding: 7px 12px;
  cursor: pointer;
  font-weight: 800;
}

.confirm-delete {
  display: flex;
  gap: 8px;
  align-items: center;
  flex-wrap: wrap;
  color: var(--sl-800);
  font-size: 0.86rem;
  font-weight: 800;
}

.confirm-delete button {
  border: 1px solid var(--border);
  background: #fff;
  color: var(--sl-800);
  padding: 6px 10px;
  cursor: pointer;
  font-weight: 800;
}

.confirm-delete button:first-of-type {
  border-color: var(--rcm-red);
  background: var(--rcm-red);
  color: #fff;
}

.delete-error {
  background: rgba(220, 38, 38, 0.1);
  color: var(--rcm-red);
  padding: 8px 10px;
  font-weight: 800;
}

@media (max-width: 920px) {
  .workspace-grid {
    grid-template-columns: 1fr;
  }

  .facts-grid {
    grid-template-columns: repeat(2, minmax(0, 1fr));
  }
}

@media (max-width: 640px) {
  .workspace-header,
  .workspace-footer,
  .next-action {
    align-items: stretch;
    flex-direction: column;
  }

  .facts-grid,
  .edit-grid {
    grid-template-columns: 1fr;
  }

  .workspace-grid {
    padding-left: 14px;
    padding-right: 14px;
  }

  .workspace-quote {
    margin-left: 14px;
    margin-right: 14px;
  }
}
</style>
