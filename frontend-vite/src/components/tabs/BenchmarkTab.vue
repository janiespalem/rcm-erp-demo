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
    <h2>📊 Benchmark Ceny / kg</h2>
    <div style="margin-bottom:20px;display:flex;gap:12px;flex-wrap:wrap">
      <div style="flex:1;min-width:150px">
        <label style="display:block;font-size:0.85rem;margin-bottom:4px;font-weight:600">Materiał:</label>
        <input v-model="filters.material" type="text" placeholder="np. S235, nierdzewka..." style="width:100%;padding:8px">
      </div>
      <div style="flex:1;min-width:150px">
        <label style="display:block;font-size:0.85rem;margin-bottom:4px;font-weight:600">Typ zlecenia:</label>
        <input v-model="filters.order_type" type="text" placeholder="np. remont, nowa_czesc..." style="width:100%;padding:8px">
      </div>
      <div style="align-self:flex-end">
        <button class="btn btn-primary" @click="load()">Szukaj</button>
      </div>
    </div>

    <div v-if="benchmarkData">
      <div style="display:grid;grid-template-columns:repeat(auto-fit,minmax(150px,1fr));gap:12px;margin-bottom:20px">
        <div style="background:#f5f5f5;border-left:4px solid #2563eb;padding:12px;border-radius:4px">
          <div style="font-size:0.8rem;color:#666;text-transform:uppercase;letter-spacing:0.05em">Średnia</div>
          <div style="font-size:1.5rem;font-weight:700;color:#1a1a2e">{{ benchmarkData.avg_pln_kg.toFixed(2) }} PLN/kg</div>
        </div>
        <div style="background:#f5f5f5;border-left:4px solid #059669;padding:12px;border-radius:4px">
          <div style="font-size:0.8rem;color:#666;text-transform:uppercase;letter-spacing:0.05em">Minimum</div>
          <div style="font-size:1.5rem;font-weight:700;color:#1a1a2e">{{ benchmarkData.min_pln_kg.toFixed(2) }} PLN/kg</div>
        </div>
        <div style="background:#f5f5f5;border-left:4px solid #dc2626;padding:12px;border-radius:4px">
          <div style="font-size:0.8rem;color:#666;text-transform:uppercase;letter-spacing:0.05em">Maximum</div>
          <div style="font-size:1.5rem;font-weight:700;color:#1a1a2e">{{ benchmarkData.max_pln_kg.toFixed(2) }} PLN/kg</div>
        </div>
        <div style="background:#f5f5f5;border-left:4px solid #f59e0b;padding:12px;border-radius:4px">
          <div style="font-size:0.8rem;color:#666;text-transform:uppercase;letter-spacing:0.05em">Próbek</div>
          <div style="font-size:1.5rem;font-weight:700;color:#1a1a2e">{{ benchmarkData.count }}</div>
        </div>
      </div>
      <div v-if="benchmarkData.warning" style="background:#fef3c7;border:1px solid #fcd34d;border-radius:6px;padding:12px;margin-bottom:20px;color:#92400e">
        ⚠️ {{ benchmarkData.warning }}
      </div>
      <div v-if="benchmarkData.samples?.length">
        <h3 style="margin-bottom:12px;font-size:1.1rem">Historyczne zlecenia:</h3>
        <table>
          <thead><tr><th>Data</th><th>Waga (kg)</th><th>Cena netto (PLN)</th><th>PLN/kg</th></tr></thead>
          <tbody>
            <tr v-for="s in benchmarkData.samples" :key="s.order_id">
              <td>{{ s.date }}</td>
              <td>{{ s.weight_kg.toFixed(1) }}</td>
              <td>{{ s.total_net.toFixed(2) }}</td>
              <td style="font-weight:700;color:#2563eb">{{ s.pln_kg.toFixed(2) }}</td>
            </tr>
          </tbody>
        </table>
      </div>
      <div v-else style="text-align:center;color:#999;padding:20px">Brak próbek dla wybranego materiału/typu.</div>
    </div>
    <div v-else style="text-align:center;color:#999;padding:40px">
      Wprowadź kryteria i kliknij "Szukaj" aby wyświetlić benchmark.
    </div>
  </div>
</template>
