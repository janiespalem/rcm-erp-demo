<script setup>
import {
  Plus, List, HelpCircle, BookOpen,
  FolderOpen, LayoutDashboard, Calendar, BarChart2,
  Settings2, Package, Archive, TrendingUp, Calculator,
} from 'lucide-vue-next'

defineProps({
  role:         { type: String, required: true },
  activeTab:    { type: String, required: true },
  pendingCount: { type: Number, default: 0 },
})
const emit = defineEmits(['switch'])
</script>

<template>
  <nav>
    <button v-if="['produkcja', 'technolog', 'ceo', 'biuro'].includes(role)" :class="{active: activeTab==='shift_reports'}" @click="emit('switch','shift_reports')">
      <List :size="15" /> Raporty zmianowe
    </button>
    <!-- BIURO: główna ścieżka = Nowe zlecenie / Zlecenia / Pytania -->
    <template v-if="role === 'biuro'">
      <button :class="{active: activeTab==='wizard'}"  @click="emit('switch','wizard')">
        <Plus :size="15" /> Nowe zlecenie
      </button>
      <button :class="{active: activeTab==='orders'}"  @click="emit('switch','orders')">
        <List :size="15" /> Zlecenia
      </button>
      <button :class="{active: activeTab==='pytania'}" @click="emit('switch','pytania')">
        <HelpCircle :size="15" /> Pytania
        <span v-if="pendingCount > 0" class="nav-badge">{{ pendingCount }}</span>
      </button>

      <span class="nav-sep" aria-hidden="true"></span>
      <button class="nav-secondary" :class="{active: activeTab==='biuro_katalog'}" @click="emit('switch','biuro_katalog')">
        <BookOpen :size="14" /> Katalog
      </button>
      <button class="nav-secondary" :class="{active: activeTab==='calculators'}" @click="emit('switch','calculators')">
        <Calculator :size="14" /> Kalkulatory
      </button>
    </template>

    <!-- TECHNOLOG: główna ścieżka = Zlecenia + Nowe zlecenie; Admin = Operacje/Materiały/Katalog PK/Rentowność -->
    <template v-if="role === 'technolog'">
      <button :class="{active: activeTab==='orders'}" @click="emit('switch','orders')">
        <List :size="15" /> Zlecenia
      </button>
      <button :class="{active: activeTab==='wizard'}" @click="emit('switch','wizard')">
        <Plus :size="15" /> Nowe zlecenie
      </button>

      <span class="nav-sep" aria-hidden="true"></span>
      <button class="nav-secondary" :class="{active: activeTab==='templates'}" @click="emit('switch','templates')">
        <FolderOpen :size="14" /> Katalog SOP
      </button>
      <button class="nav-secondary" :class="{active: activeTab==='calculators'}" @click="emit('switch','calculators')">
        <Calculator :size="14" /> Kalkulatory
      </button>

      <span class="nav-sep" aria-hidden="true"></span>
      <span class="nav-group-label">Admin</span>
      <button class="nav-secondary" :class="{active: activeTab==='operations'}" @click="emit('switch','operations')">
        <Settings2 :size="14" /> Operacje
      </button>
      <button class="nav-secondary" :class="{active: activeTab==='materials'}"  @click="emit('switch','materials')">
        <Package :size="14" /> Materiały
      </button>
      <button class="nav-secondary" :class="{active: activeTab==='projects'}"   @click="emit('switch','projects')">
        <Archive :size="14" /> Katalog PK
      </button>
      <button class="nav-secondary" :class="{active: activeTab==='rentownosc'}" @click="emit('switch','rentownosc')">
        <TrendingUp :size="14" /> Rentowność
      </button>
    </template>

    <!-- CEO: bez zmian na tym etapie -->
    <template v-if="role === 'ceo'">
      <button :class="{active: activeTab==='analytics'}"   @click="emit('switch','analytics')">
        <LayoutDashboard :size="15" /> Dashboard
      </button>
      <button :class="{active: activeTab==='harmonogram'}" @click="emit('switch','harmonogram')">
        <Calendar :size="15" /> Harmonogram
      </button>
      <button :class="{active: activeTab==='benchmark'}"   @click="emit('switch','benchmark')">
        <BarChart2 :size="15" /> Benchmark cen
      </button>
      <button :class="{active: activeTab==='orders'}"      @click="emit('switch','orders')">
        <List :size="15" /> Wszystkie zlecenia
      </button>
    </template>

  </nav>
</template>

<style scoped>
.nav-badge {
  background: var(--red);
  color: #fff;
  border-radius: 10px;
  padding: 1px 6px;
  font-size: 0.68rem;
  font-weight: 700;
  font-family: var(--font-data);
  line-height: 1.4;
}

/* Divider between the core workflow path and secondary/admin tabs */
.nav-sep {
  width: 1px;
  align-self: stretch;
  margin: 4px 6px;
  background: var(--border);
}

.nav-group-label {
  align-self: center;
  font-size: 0.66rem;
  font-weight: 700;
  letter-spacing: 0.08em;
  text-transform: uppercase;
  color: var(--muted);
  padding: 0 2px;
}

/* Secondary/admin tabs are visually de-emphasised vs the core path */
.nav-secondary {
  font-size: 0.82rem;
  color: var(--muted);
  opacity: 0.85;
}
.nav-secondary:hover { opacity: 1; }
.nav-secondary.active { opacity: 1; color: var(--sl-900); }
</style>
