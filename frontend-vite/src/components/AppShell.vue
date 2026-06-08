<script setup>
import { ref, onMounted, defineAsyncComponent } from 'vue'
import HeaderBar          from './HeaderBar.vue'
import TabNav             from './TabNav.vue'
import ToastNotifications from './ToastNotifications.vue'
import ConfirmDialog      from './ConfirmDialog.vue'
import { useAuth }              from '@/composables/useAuth'
import { useOrders }            from '@/composables/useOrders'
import { useTemplates }         from '@/composables/useTemplates'
import { useApprovedMaterials } from '@/composables/useApprovedMaterials'
import { useSettings }          from '@/composables/useSettings'
import { useNotifications }     from '@/composables/useNotifications'

// ── Lazy-loaded tab components ────────────────────────────────────────────────
const WizardTab       = defineAsyncComponent(() => import('./tabs/WizardTab.vue'))
const OrdersTab       = defineAsyncComponent(() => import('./tabs/OrdersTab.vue'))
const TemplatesTab    = defineAsyncComponent(() => import('./tabs/TemplatesTab.vue'))
const AnalyticsTab    = defineAsyncComponent(() => import('./tabs/AnalyticsTab.vue'))
const HarmonogramTab  = defineAsyncComponent(() => import('./tabs/HarmonogramTab.vue'))
const BenchmarkTab    = defineAsyncComponent(() => import('./tabs/BenchmarkTab.vue'))
const PytaniaTab      = defineAsyncComponent(() => import('./tabs/PytaniaTab.vue'))
const BiuroKatalogTab = defineAsyncComponent(() => import('./tabs/BiuroKatalogTab.vue'))
const OperationsTab   = defineAsyncComponent(() => import('./tabs/OperationsTab.vue'))
const MaterialsTab    = defineAsyncComponent(() => import('./tabs/MaterialsTab.vue'))
const ProjectsTab     = defineAsyncComponent(() => import('./tabs/ProjectsTab.vue'))
const RentownoscTab   = defineAsyncComponent(() => import('./tabs/RentownoscTab.vue'))
const LegoCalcTab     = defineAsyncComponent(() => import('./tabs/LegoCalcTab.vue'))

const { currentUser, logout } = useAuth()
const { loadOrders }          = useOrders()
const { loadTemplates }       = useTemplates()
const { loadApprovedMaterials } = useApprovedMaterials()
const { loadSettings }        = useSettings()
const { loadPytania, pendingCount } = useNotifications()

// ── Init state ────────────────────────────────────────────────────────────────
const initError   = ref(null)
const initLoading = ref(true)

// ── Tab state ─────────────────────────────────────────────────────────────────
const DEFAULT_TAB = {
  biuro: 'wizard', technolog: 'orders', ceo: 'analytics', dyrektor_produkcji: 'orders',
}
const tab = ref(DEFAULT_TAB[currentUser.value?.role] || 'wizard')

// ── Tab switching ─────────────────────────────────────────────────────────────
function switchTab(name) {
  tab.value = name
}

// ── Global data load on mount ─────────────────────────────────────────────────
onMounted(async () => {
  try {
    await Promise.all([
      loadOrders(),
      loadTemplates(),
      loadApprovedMaterials(),
      loadSettings(),
      loadPytania(),
    ])
  } catch (e) {
    initError.value = e?.message || 'Nie udało się załadować danych. Sprawdź połączenie z serwerem.'
  } finally {
    initLoading.value = false
  }
})

function handleLogout() {
  logout()
}

function reloadPage() {
  window.location.reload()
}
</script>

<template>
  <HeaderBar :user="currentUser" @logout="handleLogout" />
  <TabNav :role="currentUser.role" :active-tab="tab" :pending-count="pendingCount" @switch="switchTab" />

  <main v-if="initError" style="display:flex;align-items:center;justify-content:center;min-height:60vh">
    <div style="background:rgba(198,40,40,.08);border:1px solid var(--rcm-red);border-radius:var(--radius);padding:24px 32px;max-width:480px;text-align:center">
      <div style="font-size:1rem;font-weight:700;color:var(--rcm-red);margin-bottom:8px">Błąd ładowania danych</div>
      <div style="font-size:0.88rem;color:var(--text);margin-bottom:16px">{{ initError }}</div>
      <button class="btn btn-outline" @click="reloadPage">Odśwież stronę</button>
    </div>
  </main>

  <main v-else-if="initLoading" style="display:flex;align-items:center;justify-content:center;min-height:60vh">
    <div style="color:var(--muted);font-size:0.95rem">Ładowanie…</div>
  </main>

  <main v-else>
    <WizardTab        v-if="tab==='wizard'"       @switch-tab="switchTab" />
    <OrdersTab        v-if="tab==='orders'"       @switch-tab="switchTab" />
    <TemplatesTab     v-if="tab==='templates'" />
    <AnalyticsTab     v-if="tab==='analytics'" />
    <HarmonogramTab   v-if="tab==='harmonogram'" />
    <BenchmarkTab     v-if="tab==='benchmark'" />
    <PytaniaTab       v-if="tab==='pytania'" />
    <BiuroKatalogTab  v-if="tab==='biuro_katalog'" />
    <OperationsTab    v-if="tab==='operations'" />
    <MaterialsTab     v-if="tab==='materials'" />
    <ProjectsTab      v-if="tab==='projects'" />
    <RentownoscTab    v-if="tab==='rentownosc'" />
    <LegoCalcTab      v-if="tab==='lego_kalk'" />
  </main>

  <ConfirmDialog />
  <ToastNotifications />
</template>
