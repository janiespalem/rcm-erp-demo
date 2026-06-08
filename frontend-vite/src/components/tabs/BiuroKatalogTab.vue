<script setup>
import { ref, computed, onMounted } from 'vue'
import { api } from '@/composables/useApi'
import { biuroTemplatePriceLabel } from '@/utils/format'

const LABELS = { remont: 'Remont / Naprawa', usługa: 'Usługi CNC', zbrojenie: 'Zbrojenie', podzespół: 'Podzespoły' }

const items    = ref([])
const catFilter = ref('')
const expanded = ref(null)

onMounted(async () => {
  items.value = await api('/templates')
})

const categories = computed(() => [...new Set(items.value.map(t => t.category))].sort())

const groups = computed(() => {
  let filtered = catFilter.value ? items.value.filter(t => t.category === catFilter.value) : items.value
  const g = {}
  for (const t of filtered) {
    const key = t.project_code || LABELS[t.category] || t.category
    if (!g[key]) g[key] = []
    g[key].push(t)
  }
  for (const k of Object.keys(g)) {
    g[k].sort((a, b) => (a.position_nr || a.name).localeCompare(b.position_nr || b.name, 'pl', { numeric: true }))
  }
  return g
})
</script>

<template>
  <div class="card">
    <div style="display:flex;justify-content:space-between;align-items:center;margin-bottom:16px">
      <h2 style="margin:0">Katalog usług i produktów</h2>
      <span style="color:var(--muted);font-size:0.82rem">{{ items.length }} pozycji</span>
    </div>
    <div style="display:flex;gap:6px;flex-wrap:wrap;margin-bottom:16px">
      <button v-for="cat in categories" :key="cat"
              @click="catFilter = (catFilter === cat ? '' : cat)"
              :style="`padding:4px 12px;border-radius:12px;border:1px solid var(--border);cursor:pointer;font-size:0.8rem;
                       background:${catFilter===cat ? 'var(--rcm-blue)' : 'transparent'};
                       color:${catFilter===cat ? '#fff' : 'var(--text)'}`">
        {{ cat }}
      </button>
    </div>
    <div v-for="(groupItems, groupName) in groups" :key="groupName" class="catalog-group">
      <div @click="expanded = (expanded === groupName ? null : groupName)" class="catalog-group-head">
        <span style="font-weight:700;font-size:0.95rem">{{ groupName }}</span>
        <div style="display:flex;align-items:center;gap:12px">
          <span style="color:var(--muted);font-size:0.8rem">{{ groupItems.length }} poz.</span>
          <span style="color:var(--muted)">{{ expanded === groupName ? '▲' : '▼' }}</span>
        </div>
      </div>
      <div v-if="expanded === groupName">
        <div class="table-shell" style="border-left:0;border-right:0;border-bottom:0;border-radius:0">
          <table style="font-size:0.85rem">
            <thead>
              <tr><th>Poz.</th><th>Nazwa</th><th>Kategoria</th><th style="text-align:right">Cena / status</th></tr>
            </thead>
            <tbody>
              <tr v-for="t in groupItems" :key="t.id">
                <td style="color:var(--muted);font-size:0.78rem;white-space:nowrap">{{ t.position_nr || '—' }}</td>
                <td>{{ t.name }}</td>
                <td><span class="catalog-chip">{{ t.category }}</span></td>
                <td style="text-align:right;font-weight:600">
                  <span :style="t.project_code ? 'color:var(--muted)' : ''">{{ biuroTemplatePriceLabel(t) }}</span>
                </td>
              </tr>
            </tbody>
          </table>
        </div>
      </div>
    </div>
    <p v-if="items.length === 0" style="color:var(--muted);padding:16px 0">Brak pozycji w katalogu</p>
  </div>
</template>
