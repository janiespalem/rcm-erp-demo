<script setup>
import { ref } from 'vue'
import { api } from '@/composables/useApi'

const benchmarkData = ref(null)
const filters = ref({ material: '', order_type: '' })

async function load() {
  const params = new URLSearchParams()
  if (filters.value.material)   params.append('material',   filters.value.material)
  if (filters.value.order_type) params.append('order_type', filters.value.order_type)
  const qs = params.toString() ? `?${params.toString()}` : ''
  try {
    benchmarkData.value = await api(`/benchmarks/price-per-kg${qs}`)
  } catch {
    benchmarkData.value = null
  }
}
</script>

<template>
  <div class="card">
    <div class="tab-toolbar">
      <div style="display:flex;gap:12px;flex-wrap:wrap;flex:1">
        <div class="form-group" style="flex:1;min-width:150px;margin-bottom:0">
          <label>Materiał</label>
          <input v-model="filters.material" type="text" placeholder="np. S235, nierdzewka...">
        </div>
        <div class="form-group" style="flex:1;min-width:150px;margin-bottom:0">
          <label>Typ zlecenia</label>
          <input v-model="filters.order_type" type="text" placeholder="np. remont, nowa_czesc...">
        </div>
      </div>
      <button class="btn btn-primary" @click="load()">Szukaj</button>
    </div>

    <div v-if="benchmarkData">
      <div style="display:grid;grid-template-columns:repeat(auto-fit,minmax(150px,1fr));gap:12px;margin-bottom:20px">
        <div style="background:var(--surface2);border-left:4px solid var(--rcm-blue);padding:12px;border-radius:4px">
          <div style="font-size:0.8rem;color:var(--muted);text-transform:uppercase;letter-spacing:0.05em">Średnia</div>
          <div style="font-size:1.5rem;font-weight:700">{{ benchmarkData.avg_pln_kg.toFixed(2) }} PLN/kg</div>
        </div>
        <div style="background:var(--surface2);border-left:4px solid var(--rcm-green);padding:12px;border-radius:4px">
          <div style="font-size:0.8rem;color:var(--muted);text-transform:uppercase;letter-spacing:0.05em">Minimum</div>
          <div style="font-size:1.5rem;font-weight:700">{{ benchmarkData.min_pln_kg.toFixed(2) }} PLN/kg</div>
        </div>
        <div style="background:var(--surface2);border-left:4px solid var(--rcm-red);padding:12px;border-radius:4px">
          <div style="font-size:0.8rem;color:var(--muted);text-transform:uppercase;letter-spacing:0.05em">Maximum</div>
          <div style="font-size:1.5rem;font-weight:700">{{ benchmarkData.max_pln_kg.toFixed(2) }} PLN/kg</div>
        </div>
        <div style="background:var(--surface2);border-left:4px solid var(--rcm-yellow, #f59e0b);padding:12px;border-radius:4px">
          <div style="font-size:0.8rem;color:var(--muted);text-transform:uppercase;letter-spacing:0.05em">Próbek</div>
          <div style="font-size:1.5rem;font-weight:700">{{ benchmarkData.count }}</div>
        </div>
      </div>
      <div v-if="benchmarkData.warning" style="background:var(--surface2);border:1px solid var(--border);border-radius:6px;padding:12px;margin-bottom:20px;color:var(--muted)">
        Uwaga: {{ benchmarkData.warning }}
      </div>
      <div v-if="benchmarkData.samples?.length">
        <div style="font-weight:700;margin-bottom:12px;font-size:0.95rem">Historyczne zlecenia</div>
        <table class="data-table">
          <thead>
            <tr>
              <th>Data</th>
              <th class="num">Waga (kg)</th>
              <th class="num">Cena netto (PLN)</th>
              <th class="num">PLN/kg</th>
            </tr>
          </thead>
          <tbody>
            <tr v-for="s in benchmarkData.samples" :key="s.order_id">
              <td>{{ s.date }}</td>
              <td class="num">{{ s.weight_kg.toFixed(1) }}</td>
              <td class="num">{{ s.total_net.toFixed(2) }}</td>
              <td class="num" style="font-weight:700;color:var(--rcm-blue)">{{ s.pln_kg.toFixed(2) }}</td>
            </tr>
          </tbody>
        </table>
      </div>
      <div v-else style="text-align:center;color:var(--muted);padding:20px">Brak próbek dla wybranego materiału/typu.</div>
    </div>
    <div v-else style="text-align:center;color:var(--muted);padding:40px">
      Wprowadź kryteria i kliknij "Szukaj" aby wyświetlić benchmark.
    </div>
  </div>
</template>
