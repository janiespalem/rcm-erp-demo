<script setup>
import { ref, computed } from 'vue'
import { useOrders } from '@/composables/useOrders'
import { useAuth } from '@/composables/useAuth'
import OrderWorkspace from '@/components/OrderWorkspace.vue'
import { moneyLabel, shortText, STATUS_PL } from '@/utils/format'
import { stageForStatus, nextStepLabel, isOrderOverdue } from '@/utils/orderWorkflow'

const emit = defineEmits(['switch-tab'])

const { orders } = useOrders()
const { currentUser } = useAuth()

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
    canCreate: false,
  },
  ceo: {
    defaultFilter: 'all',
    title: 'Wszystkie zlecenia',
    subtitle: 'Podgląd całego warsztatu.',
    canCreate: false,
  },
  dyrektor_produkcji: {
    defaultFilter: 'all',
    title: 'Zlecenia',
    subtitle: 'Cały warsztat — pełny dostęp do każdego etapu.',
    canCreate: true,
  },
}

const roleView = computed(() => ROLE_VIEW[role.value] || ROLE_VIEW.dyrektor_produkcji)

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
    ...STAGES.map(stage => ({ key: stage.key, label: stage.label })),
  ]
  return ROLE_QUEUE_STAGES[role.value]
    ? [{ key: 'queue', label: 'Moja kolejka' }, ...base]
    : base
})

const activeFilter = ref(roleView.value.defaultFilter)

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

function selectOrder(order) {
  selectedId.value = order.id
}

function clearSelection() {
  selectedId.value = null
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

const filteredOrders = computed(() => {
  if (activeFilter.value === 'all') return orders.value
  if (activeFilter.value === 'queue') {
    const stages = ROLE_QUEUE_STAGES[role.value] || []
    return stages.length ? orders.value.filter(o => stages.includes(stageForStatus(o.status))) : orders.value
  }
  return orders.value.filter(order => stageForStatus(order.status) === activeFilter.value)
})

const selectedOrder = computed(() =>
  orders.value.find(order => order.id === selectedId.value) || null
)

const filterCounts = computed(() => {
  const stages = ROLE_QUEUE_STAGES[role.value] || []
  return {
    queue: stages.length ? orders.value.filter(o => stages.includes(stageForStatus(o.status))).length : 0,
    all: orders.value.length,
    ...Object.fromEntries(stageStats.value.map(stage => [stage.key, stage.count])),
  }
})
</script>

<template>
  <section class="orders-workbench" :class="{ 'detail-open': selectedOrder }">
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

      <div class="pipeline-grid" aria-label="Etapy zleceń">
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
              <th>Klient / opis</th>
              <th>Status</th>
              <th>Termin</th>
              <th>Co dalej</th>
              <th>Wartość</th>
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
                <span v-if="order.is_defence">MON</span>
              </td>
              <td class="client-cell">
                <strong>{{ order.client || 'Bez klienta' }}</strong>
                <span>{{ orderDescription(order) }}</span>
                <small v-if="orderMeta(order)">{{ orderMeta(order) }}</small>
              </td>
              <td>
                <span :class="'badge badge-' + (order.status || 'draft')">
                  {{ STATUS_PL[order.status] || order.status || '—' }}
                </span>
              </td>
              <td class="deadline-cell" :class="{ overdue: isOrderOverdue(order) }">
                {{ order.deadline || '—' }}
              </td>
              <td class="next-step-cell">{{ nextStepLabel(order.status) }}</td>
              <td class="value-cell">{{ orderValue(order) }}</td>
            </tr>

            <tr v-if="filteredOrders.length === 0">
              <td colspan="6" class="empty-cell">Brak zleceń w tym etapie</td>
            </tr>
          </tbody>
        </table>
      </div>
    </div>

    <aside class="workbench-detail" aria-label="Workspace zlecenia">
      <button
        v-if="selectedOrder"
        class="detail-back"
        type="button"
        @click="clearSelection"
      >
        ← Wszystkie zlecenia
      </button>
      <OrderWorkspace
        v-if="selectedOrder"
        :key="selectedId"
        :order="selectedOrder"
        @close="clearSelection"
        @saved="clearSelection"
      />
      <div v-else class="workspace-placeholder">
        <h3>Wybierz zlecenie</h3>
        <p>Kliknij pozycję z listy, żeby pracować na wycenie, dokumentach, plikach i pytaniach bez otwierania kolejnej modalki.</p>
      </div>
    </aside>
  </section>
</template>

<style scoped>
.orders-workbench {
  display: grid;
  grid-template-columns: minmax(520px, 0.95fr) minmax(430px, 1.05fr);
  align-items: start;
  gap: 14px;
}

.workbench-master {
  display: grid;
  gap: 14px;
  min-width: 0;
}

.workbench-detail {
  position: sticky;
  top: 14px;
  display: grid;
  gap: 10px;
  min-width: 0;
  max-height: calc(100vh - 28px);
  overflow: auto;
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

.pipeline-grid {
  display: grid;
  grid-template-columns: repeat(4, minmax(0, 1fr));
  gap: 10px;
}

.pipeline-card {
  min-height: 104px;
  border: 1px solid var(--border);
  border-radius: 8px;
  background: #fff;
  color: var(--text);
  padding: 14px;
  text-align: left;
  cursor: pointer;
  display: flex;
  flex-direction: column;
  justify-content: space-between;
  gap: 8px;
  transition: border-color 0.15s, box-shadow 0.15s, transform 0.15s;
}

.pipeline-card:hover {
  border-color: var(--rcm-accent);
  box-shadow: var(--sh-sm);
  transform: translateY(-1px);
}

.pipeline-card.active {
  border-color: var(--rcm-blue);
  box-shadow: inset 0 0 0 1px var(--rcm-blue), var(--sh-sm);
}

.pipeline-card-head {
  display: flex;
  justify-content: space-between;
  align-items: flex-start;
  gap: 10px;
  font-weight: 800;
}

.pipeline-card-head strong {
  color: var(--rcm-blue);
  font-family: var(--font-data);
  font-size: 1.45rem;
  line-height: 1;
}

.pipeline-hint {
  color: var(--muted);
  font-size: 0.78rem;
}

.pipeline-overdue {
  width: fit-content;
  border-radius: 999px;
  background: rgba(198, 40, 40, 0.1);
  color: var(--rcm-red);
  padding: 3px 8px;
  font-size: 0.72rem;
  font-weight: 700;
}

.filter-bar {
  display: flex;
  gap: 6px;
  flex-wrap: wrap;
}

.filter-btn {
  height: 30px;
  border: 1px solid var(--border);
  border-radius: 999px;
  background: #fff;
  color: var(--muted);
  padding: 0 12px;
  font-size: 0.82rem;
  font-weight: 700;
  display: inline-flex;
  align-items: center;
  gap: 7px;
  cursor: pointer;
}

.filter-btn span {
  min-width: 20px;
  border-radius: 999px;
  background: var(--sl-100);
  color: var(--sl-600);
  padding: 1px 6px;
  text-align: center;
  font-family: var(--font-data);
  font-size: 0.72rem;
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
  min-width: 860px;
}

.orders-table tbody tr {
  cursor: pointer;
  transition: background 0.12s, box-shadow 0.12s;
}

.orders-table tbody tr:hover {
  background: var(--sl-50);
  box-shadow: inset 3px 0 0 var(--rcm-accent);
}

.orders-table tbody tr.is-selected {
  background: rgba(30, 64, 175, 0.08);
  box-shadow: inset 3px 0 0 var(--rcm-blue);
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
.client-cell strong,
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

.client-cell {
  min-width: 260px;
}

.client-cell strong,
.client-cell span,
.client-cell small {
  display: block;
}

.client-cell span {
  margin-top: 3px;
  color: var(--text);
  font-size: 0.86rem;
  line-height: 1.35;
}

.client-cell small {
  margin-top: 4px;
  color: var(--muted);
  font-size: 0.74rem;
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
  color: var(--muted);
  font-weight: 700;
  white-space: nowrap;
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

.detail-back {
  display: none;
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

.workspace-placeholder {
  min-height: 420px;
  border: 1px dashed var(--border);
  border-radius: 8px;
  background: #fff;
  display: grid;
  place-content: center;
  gap: 8px;
  padding: 28px;
  text-align: center;
}

.workspace-placeholder h3 {
  margin: 0;
  color: var(--rcm-blue);
  font-size: 1rem;
  font-weight: 850;
}

.workspace-placeholder p {
  max-width: 360px;
  margin: 0;
  color: var(--muted);
  font-size: 0.88rem;
  line-height: 1.5;
}

@media (max-width: 1280px) {
  .orders-workbench {
    grid-template-columns: minmax(460px, 0.9fr) minmax(380px, 1.1fr);
  }
}

@media (max-width: 1100px) {
  .orders-workbench {
    grid-template-columns: 1fr;
  }

  .workbench-detail {
    position: static;
    max-height: none;
    overflow: visible;
  }

  .orders-workbench.detail-open .workbench-master {
    display: none;
  }

  .orders-workbench:not(.detail-open) .workbench-detail {
    display: none;
  }

  .detail-back {
    display: inline-flex;
  }
}

@media (max-width: 900px) {
  .pipeline-grid {
    grid-template-columns: repeat(2, minmax(0, 1fr));
  }
}

@media (max-width: 560px) {
  .workbench-header {
    display: block;
  }

  .pipeline-grid {
    grid-template-columns: 1fr;
  }
}
</style>
