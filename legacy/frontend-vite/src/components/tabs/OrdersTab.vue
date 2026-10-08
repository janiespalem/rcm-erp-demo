<script setup>
import { ref, computed, watch, onMounted } from 'vue'
import { api } from '@/composables/useApi'
import { useOrders } from '@/composables/useOrders'
import { useAuth } from '@/composables/useAuth'
import { useConfirm } from '@/composables/useConfirm'
import OrderWorkspace from '@/components/OrderWorkspace.vue'
import QuotePanel from '@/components/QuotePanel.vue'
import OrderHistory from '@/components/OrderHistory.vue'
import { moneyLabel, shortText, STATUS_PL } from '@/utils/format'
import { stageForStatus, nextStepFor, isOrderOverdue } from '@/utils/orderWorkflow'

const emit = defineEmits(['switch-tab'])

const {
  orders, archivedOrders, focusOrderId, loadOrders,
  archiveOrder, restoreOrder, confirmOrder, deliverOrder,
} = useOrders()
const { currentUser } = useAuth()
const { confirm } = useConfirm()

const role = computed(() => currentUser.value?.role)

// Stages shown in "Moja kolejka" per role — combines the two stages each role owns.
const ROLE_QUEUE_STAGES = {
  biuro: ['wycena', 'gotowe'],
  technolog: ['nowe', 'produkcja'],
}

// Role-native landing: each role opens to the stage that matters most for its job,
// with a headline framed as that role's worklist. All filters stay reachable.
const ROLE_VIEW = {
  biuro: {
    defaultFilter: 'queue',
    title: 'Zlecenia',
    subtitle: 'Twoja kolejka: wyceny do zatwierdzenia i zlecenia gotowe do wydania.',
    canCreate: true,
  },
  technolog: {
    defaultFilter: 'queue',
    title: 'Zlecenia',
    subtitle: 'Twoja kolejka: nowe do wyceny i zlecenia w produkcji.',
    canCreate: true,
  },
  ceo: {
    defaultFilter: 'all',
    title: 'Wszystkie zlecenia',
    subtitle: 'Podgląd całego warsztatu.',
    canCreate: false,
  },
}

const roleView = computed(() => ROLE_VIEW[role.value] || ROLE_VIEW.technolog)

const selectedId = ref(null)

const STAGES = [
  { key: 'nowe', label: 'Nowe', hint: 'do triage i wyceny' },
  { key: 'wycena', label: 'Wycena', hint: 'do zatwierdzenia' },
  { key: 'produkcja', label: 'Produkcja', hint: 'w realizacji' },
  { key: 'gotowe', label: 'Gotowe', hint: 'do wydania' },
]

const FILTERS = computed(() => {
  const base = [
    { key: 'all', label: 'Wszystkie' },
    { key: 'archive', label: 'Archiwum' },
  ]
  return ROLE_QUEUE_STAGES[role.value]
    ? [{ key: 'queue', label: 'Moja kolejka' }, ...base]
    : base
})

const activeFilter = ref(roleView.value.defaultFilter)

// A freshly created order lands in stage "nowe", which biuro's default queue
// (wycena/gotowe) hides — open it explicitly so the user sees where it went.
watch([orders, focusOrderId], () => {
  if (!focusOrderId.value) return
  const order = orders.value.find(o => o.id === focusOrderId.value)
  if (!order) return
  activeFilter.value = stageForStatus(order.status)
  selectedId.value = order.id
  focusOrderId.value = null
}, { immediate: true })

function nextStep(order) {
  return nextStepFor(order, role.value)
}

// Unanswered questions per order: biuro reads it as "answer me", technolog as
// "blocked, waiting for Biuro" — both need it to triage their queue.
const pendingQuestionCounts = ref({})

onMounted(async () => {
  try {
    const params = await api('/params?status=pending')
    const counts = {}
    for (const p of params) counts[p.order_id] = (counts[p.order_id] || 0) + 1
    pendingQuestionCounts.value = counts
  } catch {
    pendingQuestionCounts.value = {}
  }
})

// Open orders that currently wait on the other side — used by the queue empty state
const waitingOnOthersCount = computed(() =>
  orders.value.filter(o => {
    const step = nextStepFor(o, role.value)
    return !step.mine && step.actor
  }).length
)

const waitingOnOthersLabel = computed(() =>
  role.value === 'technolog'
    ? 'po stronie Biura lub klienta'
    : 'po stronie Technologa lub warsztatu'
)

function orderValue(order) {
  const value = order.estimated_value ?? order.quote_total_net ?? order.total_net ?? order.quote?.total_net
  return moneyLabel(value)
}

function orderDescription(order) {
  return shortText(order.description || order.notes || order.order_type || 'Bez opisu', 92)
}

function orderMeta(order) {
  const parts = []
  if (order.quantity) parts.push(`${order.quantity} szt.`)
  if (order.material) parts.push(order.material)
  return parts.join(' · ')
}

function rowClass(order) {
  return [
    `stage-${stageForStatus(order.status)}`,
    isOrderOverdue(order) ? 'is-overdue' : '',
    order.is_defence ? 'is-defence' : '',
    selectedId.value === order.id ? 'is-selected' : '',
  ]
}

// Statuses where the technolog's job IS pricing — clicking such a row jumps
// straight into wycena (no workspace detour, no "Wyceń zlecenie" click).
const QUOTABLE_STATUSES = ['standard', 'niestandard', 'quoted']
// Wewnętrzne trafiają od razu do produkcji; wycena (operacje/materiały) jest
// opcjonalna — zielony "Wyceń" pod "Zakończ". Dla starszych internal-standard
// przycisk główny i tak otwiera wycenę, więc dokładamy go tylko w produkcji.
function canWycen(order) {
  return order.is_internal && role.value === 'technolog' && order.status === 'in_production'
}

function selectOrder(order) {
  if (role.value === 'technolog' && QUOTABLE_STATUSES.includes(order.status)) {
    quoteOrderId.value = order.id
    return
  }
  selectedId.value = order.id
}

// Historia jednym kliknięciem — z listy (każdy status, każda rola) i z wyceny.
const historyOrder = ref(null)
function openHistory(order) {
  historyOrder.value = order
}

// "Co dalej" wykonywane 1 kliknięciem z listy — bez otwierania zlecenia.
// Wycena → otwiera panel; reszta → akcja statusu (confirm/complete/deliver).
const advancing = ref(null)
async function runNextStep(order) {
  if (!nextStepFor(order, role.value).mine || advancing.value) return
  if (['standard', 'niestandard'].includes(order.status)) {
    quoteOrderId.value = order.id
    return
  }
  // in_production zamykamy jednym krokiem (deliver → wydane); gotowe = legacy.
  const fn = { quoted: confirmOrder, in_production: deliverOrder, gotowe: deliverOrder }[order.status]
  if (!fn) { selectOrder(order); return }
  advancing.value = order.id
  try {
    await fn(order.id)
    await loadOrders()
  } finally {
    advancing.value = null
  }
}

const archiving = ref(null)
function canArchive(order) {
  return role.value === 'technolog' && ['wydane', 'rejected'].includes(order.status)
}

async function archive(order) {
  const accepted = await confirm(
    `Przenieść zlecenie ${order.order_number || '#' + order.id} do archiwum?`,
    { confirmLabel: 'Archiwizuj' },
  )
  if (!accepted) return
  archiving.value = order.id
  try {
    await archiveOrder(order.id)
    if (selectedId.value === order.id) selectedId.value = null
  } finally {
    archiving.value = null
  }
}

async function restore(order) {
  archiving.value = order.id
  try {
    await restoreOrder(order.id)
    if (selectedId.value === order.id) selectedId.value = null
  } finally {
    archiving.value = null
  }
}

function clearSelection() {
  selectedId.value = null
  quoteOrderId.value = null
}

// Wycena is a full-width mode of this screen (not a panel inside the order
// card): the workspace asks us to open it, we render only the QuotePanel.
const quoteOrderId = ref(null)
const quoteOrder = computed(() =>
  orders.value.find(order => order.id === quoteOrderId.value) || null
)

function openQuote() {
  quoteOrderId.value = selectedId.value
}

function closeQuote() {
  quoteOrderId.value = null
}

async function onQuoteSaved() {
  quoteOrderId.value = null
  await loadOrders()
}

const stageStats = computed(() =>
  STAGES.map(stage => {
    const stageOrders = orders.value.filter(order => stageForStatus(order.status) === stage.key)
    return {
      ...stage,
      count: stageOrders.length,
      overdue: stageOrders.filter(isOrderOverdue).length,
    }
  })
)

// "Moja kolejka" = zlecenia czekające na MOJE działanie (nextStep.mine).
// To wyklucza wydane/zakończone/odrzucone — inaczej zamknięte zlecenia zaśmiecają
// kolejkę (etap "gotowe" sklejał statusy gotowe I wydane).
function inMyQueue(order) {
  return nextStepFor(order, role.value).mine === true
}

const filteredOrders = computed(() => {
  if (activeFilter.value === 'archive') return archivedOrders.value
  if (activeFilter.value === 'all') return orders.value
  if (activeFilter.value === 'queue') {
    return ROLE_QUEUE_STAGES[role.value] ? orders.value.filter(inMyQueue) : orders.value
  }
  return orders.value.filter(order => stageForStatus(order.status) === activeFilter.value)
})

const selectedOrder = computed(() =>
  [...orders.value, ...archivedOrders.value].find(order => order.id === selectedId.value) || null
)

const filterCounts = computed(() => {
  return {
    queue: ROLE_QUEUE_STAGES[role.value] ? orders.value.filter(inMyQueue).length : 0,
    all: orders.value.length,
    archive: archivedOrders.value.length,
    ...Object.fromEntries(stageStats.value.map(stage => [stage.key, stage.count])),
  }
})
</script>

<template>
  <section v-if="quoteOrder" class="orders-quote-mode" aria-label="Wycena">
    <QuotePanel
      :order="quoteOrder"
      :show-close="true"
      @close="closeQuote"
      @saved="onQuoteSaved"
      @history="openHistory(quoteOrder)"
    />
  </section>

  <section v-else class="orders-workbench" :class="{ 'detail-open': selectedOrder }">
    <div class="workbench-master">
      <header class="workbench-header">
        <div>
          <h2>{{ roleView.title }}</h2>
          <p>{{ roleView.subtitle }}</p>
        </div>
        <button
          v-if="roleView.canCreate"
          class="btn btn-primary create-cta"
          type="button"
          @click="emit('switch-tab', 'wizard')"
        >
          + Nowe zlecenie
        </button>
      </header>

      <div class="pipeline-strip" aria-label="Etapy zleceń">
        <button
          v-for="stage in stageStats"
          :key="stage.key"
          type="button"
          class="pipeline-card"
          :class="{ active: activeFilter === stage.key }"
          :aria-pressed="activeFilter === stage.key"
          @click="activeFilter = stage.key"
        >
          <span class="pipeline-card-head">
            <span>{{ stage.label }}</span>
            <strong>{{ stage.count }}</strong>
          </span>
          <span class="pipeline-hint">{{ stage.hint }}</span>
          <span v-if="stage.overdue" class="pipeline-overdue">{{ stage.overdue }} po terminie</span>
        </button>
      </div>

      <div class="filter-bar" aria-label="Filtry zleceń">
        <button
          v-for="filter in FILTERS"
          :key="filter.key"
          type="button"
          class="filter-btn"
          :class="{ active: activeFilter === filter.key }"
          @click="activeFilter = filter.key"
        >
          {{ filter.label }}
          <span>{{ filterCounts[filter.key] || 0 }}</span>
        </button>
      </div>

      <div class="table-shell orders-table-shell">
        <table class="orders-table">
          <thead>
            <tr>
              <th>Nr</th>
              <th>Klient</th>
              <th>Opis</th>
              <th class="num">Ilość</th>
              <th>Materiał</th>
              <th>Status</th>
              <th>Termin</th>
              <th>Co dalej</th>
              <th class="num">Wartość</th>
              <th class="hist-col" aria-label="Historia"></th>
            </tr>
          </thead>
          <tbody>
            <tr
              v-for="order in filteredOrders"
              :key="order.id"
              :class="rowClass(order)"
              tabindex="0"
              @click="selectOrder(order)"
              @keydown.enter="selectOrder(order)"
            >
              <td class="order-number-cell">
                <strong>{{ order.order_number || '#' + order.id }}</strong>
                <span v-if="order.is_defence">Restricted</span>
              </td>
              <td class="client-cell">{{ order.client || 'Bez klienta' }}</td>
              <td class="desc-cell" :title="order.description || order.notes || ''">{{ orderDescription(order) }}</td>
              <td class="qty-cell num">{{ order.quantity ? order.quantity + ' szt.' : '—' }}</td>
              <td class="mat-cell">{{ order.material || '—' }}</td>
              <td>
                <span :class="'badge badge-' + (order.status || 'draft')">
                  {{ STATUS_PL[order.status] || order.status || '—' }}
                </span>
                <span
                  v-if="pendingQuestionCounts[order.id]"
                  class="question-chip"
                  title="Pytania bez odpowiedzi"
                >! {{ pendingQuestionCounts[order.id] }}</span>
              </td>
              <td class="deadline-cell" :class="{ overdue: isOrderOverdue(order) }">
                {{ order.deadline || '—' }}
              </td>
              <td class="next-step-cell">
                <button
                  v-if="nextStep(order).mine"
                  type="button"
                  class="step-action"
                  :disabled="advancing === order.id"
                  @click.stop="runNextStep(order)"
                >{{ advancing === order.id ? '…' : nextStep(order).label }}</button>
                <span v-else-if="nextStep(order).actor" class="step-wait">{{ nextStep(order).actor }}: {{ nextStep(order).label }}</span>
                <span v-else class="step-wait">{{ nextStep(order).label }}</span>
                <button
                  v-if="canWycen(order)"
                  type="button"
                  class="step-wycen"
                  title="Operacje, materiały, wycena"
                  @click.stop="quoteOrderId = order.id"
                >Wyceń</button>
                <button
                  v-if="activeFilter !== 'archive' && canArchive(order)"
                  type="button"
                  class="btn btn-outline btn-sm"
                  :disabled="archiving === order.id"
                  @click.stop="archive(order)"
                >{{ archiving === order.id ? '…' : 'Archiwizuj' }}</button>
                <button
                  v-if="activeFilter === 'archive' && role === 'technolog'"
                  type="button"
                  class="btn btn-outline btn-sm"
                  :disabled="archiving === order.id"
                  @click.stop="restore(order)"
                >{{ archiving === order.id ? '…' : 'Przywróć' }}</button>
              </td>
              <td class="value-cell num">{{ orderValue(order) }}</td>
              <td class="hist-cell">
                <button
                  type="button"
                  class="hist-btn"
                  title="Historia zlecenia"
                  aria-label="Historia zlecenia"
                  @click.stop="openHistory(order)"
                >🕘 Historia</button>
              </td>
            </tr>

            <tr v-if="filteredOrders.length === 0">
              <td v-if="activeFilter === 'queue'" colspan="10" class="empty-cell">
                <strong class="empty-queue-title">Nic nie czeka na Ciebie ✓</strong>
                <span v-if="waitingOnOthersCount" class="empty-queue-sub">
                  {{ waitingOnOthersCount }} otwartych zleceń jest {{ waitingOnOthersLabel }}.
                </span>
                <button type="button" class="btn btn-outline btn-sm" @click="activeFilter = 'all'">
                  Pokaż wszystkie zlecenia
                </button>
              </td>
              <td v-else colspan="10" class="empty-cell">Brak zleceń w tym etapie</td>
            </tr>
          </tbody>
        </table>
      </div>
    </div>

    <aside v-if="selectedOrder" class="workbench-detail" aria-label="Workspace zlecenia">
      <button class="detail-back" type="button" @click="clearSelection">
        ← Wszystkie zlecenia
      </button>
      <OrderWorkspace
        :key="selectedId"
        :order="selectedOrder"
        @close="clearSelection"
        @saved="clearSelection"
        @open-quote="openQuote"
      />
    </aside>
  </section>

  <OrderHistory v-if="historyOrder" :order="historyOrder" @close="historyOrder = null" />
</template>

<style scoped>
/* Historia — przycisk w każdym wierszu, dostępny niezależnie od statusu/roli. */
.hist-col { width: 1%; }
.hist-cell { white-space: nowrap; text-align: right; }
.hist-btn {
  border: 1px solid var(--border);
  border-radius: 999px;
  background: var(--card);
  color: var(--sl-700);
  padding: 3px 10px;
  cursor: pointer;
  font-size: 0.74rem;
  font-weight: 800;
}
.hist-btn:hover { border-color: var(--rcm-blue); color: var(--rcm-blue); }

/* Tryb wyceny: kontener o stałej wysokości (viewport minus globalny chrome
   header 52 + nav 42 + marginesy main ≈ 154px), żeby panel wyceny przewijał
   się WEWNĄTRZ siebie — nagłówek i dolny pasek zostają nieruchome.
   ponytail: 160px to suma globalnego chrome; gdyby zmieniły się wysokości
   header/nav, poprawić tu. */
.orders-quote-mode {
  height: calc(100dvh - 160px);
  min-height: 460px;
  overflow: hidden;
}

/* Zlecenia = full-width list; selecting an order opens the workspace as a
   full-width takeover (the list hides), so neither view is cramped. */
.orders-workbench {
  display: block;
}

.workbench-master {
  display: grid;
  gap: 14px;
  min-width: 0;
}

.orders-workbench.detail-open .workbench-master {
  display: none;
}

.workbench-detail {
  display: grid;
  gap: 10px;
  min-width: 0;
}

.workbench-header {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 16px;
}

.create-cta {
  flex-shrink: 0;
  white-space: nowrap;
  font-weight: 800;
}

.workbench-header h2 {
  margin: 0;
  color: var(--rcm-blue);
  font-size: 1.12rem;
  font-weight: 800;
}

.workbench-header p {
  margin: 5px 0 0;
  color: var(--muted);
  font-size: 0.88rem;
}

.pipeline-strip {
  display: grid;
  grid-template-columns: repeat(4, minmax(0, 1fr));
  gap: 0;
  overflow: hidden;
  border: 1px solid var(--border);
  border-radius: 8px;
  background: #fff;
}

.pipeline-card {
  min-height: 46px;
  border: 0;
  border-right: 1px solid var(--border-soft);
  border-radius: 0;
  background: transparent;
  color: var(--text);
  padding: 8px 12px;
  text-align: left;
  cursor: pointer;
  display: flex;
  flex-direction: column;
  justify-content: center;
  gap: 2px;
  transition: background 0.12s, box-shadow 0.12s;
}

.pipeline-card:last-child {
  border-right: 0;
}

.pipeline-card:hover {
  background: var(--sl-50);
}

.pipeline-card.active {
  box-shadow: inset 0 -3px 0 var(--rcm-blue);
  background: var(--sl-50);
}

.pipeline-card-head {
  display: flex;
  justify-content: space-between;
  align-items: center;
  gap: 8px;
  font-weight: 800;
  font-size: 0.84rem;
}

.pipeline-card-head strong {
  color: var(--rcm-blue);
  font-family: var(--font-data);
  font-size: 1rem;
  line-height: 1;
}

.pipeline-hint {
  color: var(--muted);
  font-size: 0.72rem;
  display: none;
}

.pipeline-card.active .pipeline-hint {
  display: block;
}

.pipeline-overdue {
  width: fit-content;
  border-radius: 999px;
  background: rgba(198, 40, 40, 0.1);
  color: var(--rcm-red);
  padding: 2px 6px;
  font-size: 0.68rem;
  font-weight: 700;
}

.filter-bar {
  display: flex;
  gap: 6px;
  flex-wrap: wrap;
}

.filter-btn {
  height: 26px;
  border: 1px solid var(--border);
  border-radius: 999px;
  background: transparent;
  color: var(--muted);
  padding: 0 10px;
  font-size: 0.78rem;
  font-weight: 700;
  display: inline-flex;
  align-items: center;
  gap: 5px;
  cursor: pointer;
}

.filter-btn span {
  min-width: 18px;
  border-radius: 999px;
  background: var(--sl-100);
  color: var(--sl-600);
  padding: 0 5px;
  text-align: center;
  font-family: var(--font-data);
  font-size: 0.7rem;
}

.filter-btn.active {
  background: var(--rcm-blue);
  border-color: var(--rcm-blue);
  color: #fff;
}

.filter-btn.active span {
  background: rgba(255, 255, 255, 0.18);
  color: #fff;
}

.orders-table-shell {
  background: #fff;
}

.orders-table {
  width: 100%;
  min-width: 920px;
}

/* Dense rows; every column shrinks to its content so the slack pools in one
   flexible column (Opis) instead of scattering into dead horizontal gaps. */
.orders-table th,
.orders-table td {
  padding: 7px 12px;
  white-space: nowrap;
}

.orders-table tbody tr {
  cursor: pointer;
  transition: background 0.12s, box-shadow 0.12s;
}

.orders-table tbody tr:hover {
  background: var(--sl-50);
  box-shadow: inset 2px 0 0 var(--border);
}

.orders-table tbody tr.is-selected {
  background: var(--sl-100);
  box-shadow: inset 3px 0 0 var(--rcm-blue);
}

.orders-table tbody tr.is-selected td.order-number-cell strong {
  color: var(--rcm-blue);
  font-weight: 900;
}

.orders-table tbody tr:focus-visible {
  outline: 2px solid var(--rcm-accent);
  outline-offset: -2px;
}

.orders-table tbody tr.is-overdue {
  background: rgba(198, 40, 40, 0.05);
}

.orders-table tbody tr.is-defence {
  box-shadow: inset 3px 0 0 var(--rcm-red);
}

.order-number-cell strong,
.client-cell,
.value-cell {
  font-weight: 800;
}

.order-number-cell span {
  display: inline-block;
  margin-left: 6px;
  border-radius: 4px;
  background: rgba(198, 40, 40, 0.1);
  color: var(--rcm-red);
  padding: 2px 5px;
  font-size: 0.68rem;
  font-weight: 800;
}

/* Opis is the one flexible column: width 100% + max-width 0 lets it take all
   leftover width and truncate with an ellipsis instead of stretching the row. */
.desc-cell {
  width: 100%;
  max-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  color: var(--muted);
  font-size: 0.84rem;
}

.qty-cell {
  color: var(--muted);
}

.mat-cell {
  color: var(--text);
  font-size: 0.84rem;
}

.deadline-cell {
  white-space: nowrap;
  font-family: var(--font-data);
}

.deadline-cell.overdue {
  color: var(--rcm-red);
  font-weight: 800;
}

.next-step-cell {
  white-space: nowrap;
  display: flex;
  flex-direction: column;
  align-items: flex-start;
  gap: 5px;
}

.step-wycen {
  border: 1px solid var(--green, #16a34a);
  border-radius: 999px;
  background: var(--green, #16a34a);
  color: #fff;
  padding: 4px 12px;
  font-size: 0.76rem;
  font-weight: 800;
  cursor: pointer;
}
.step-wycen:hover { filter: brightness(1.08); }

.step-action {
  display: inline-flex;
  align-items: center;
  border: 1px solid var(--rcm-blue);
  border-radius: 999px;
  background: var(--rcm-blue);
  color: #fff;
  padding: 4px 12px;
  font-size: 0.76rem;
  font-weight: 800;
  cursor: pointer;
}
.step-action:hover { filter: brightness(1.08); }
.step-action:disabled { opacity: 0.6; cursor: default; }

.step-wait {
  color: var(--muted);
  font-weight: 700;
  font-size: 0.8rem;
}

.question-chip {
  display: inline-block;
  margin-left: 6px;
  border-radius: 999px;
  background: var(--rcm-warn);
  border: 1px solid var(--rcm-warn);
  color: #fff;
  padding: 1px 7px;
  font-size: 0.7rem;
  font-weight: 800;
  white-space: nowrap;
  letter-spacing: 0.01em;
}

.value-cell {
  text-align: right;
  white-space: nowrap;
  font-family: var(--font-data);
}

.empty-cell {
  padding: 28px;
  color: var(--muted);
  text-align: center;
}

.empty-queue-title {
  display: block;
  color: var(--text);
  font-size: 0.95rem;
  margin-bottom: 4px;
}

.empty-queue-sub {
  display: block;
  margin-bottom: 12px;
}

.orders-table .num {
  text-align: right;
}

.detail-back {
  display: inline-flex;
  width: fit-content;
  border: 1px solid var(--border);
  border-radius: 999px;
  background: #fff;
  color: var(--sl-700);
  padding: 7px 11px;
  cursor: pointer;
  font-size: 0.82rem;
  font-weight: 800;
}

@media (max-width: 900px) {
  .pipeline-strip {
    grid-template-columns: repeat(2, minmax(0, 1fr));
  }

  .pipeline-card:nth-child(2) {
    border-right: 0;
  }

  .pipeline-card:nth-child(-n + 2) {
    border-bottom: 1px solid var(--border-soft);
  }
}

@media (max-width: 560px) {
  .workbench-header {
    display: block;
  }

  .pipeline-strip {
    grid-template-columns: 1fr;
  }

  .pipeline-card,
  .pipeline-card:nth-child(2) {
    border-right: 0;
  }

  .pipeline-card:nth-child(-n + 3) {
    border-bottom: 1px solid var(--border-soft);
  }
}
</style>
