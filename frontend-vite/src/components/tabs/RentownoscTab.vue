<script setup>
import { ref, onMounted } from 'vue'
import { api } from '@/composables/useApi'

const rentownoscData = ref([])
async function load() { rentownoscData.value = await api('/rentownosc') }
onMounted(load)
</script>

<template>
  <div>
    <div style="display:flex;justify-content:space-between;align-items:center;margin-bottom:12px">
      <h2 style="font-size:1rem;font-weight:700;color:var(--rcm-blue)">Rentowność zleceń</h2>
      <button class="btn btn-outline btn-sm" @click="load()">🔄 Odśwież</button>
    </div>
    <div v-if="!rentownoscData.length" style="text-align:center;padding:32px;color:var(--muted)">
      Brak danych — wyceny zleceń wymagane
    </div>
    <div v-else style="overflow-x:auto">
      <table style="width:100%;border-collapse:collapse;font-size:0.85rem">
        <thead>
          <tr style="background:var(--surface2);text-align:left">
            <th style="padding:8px 12px">Nr zlecenia</th>
            <th style="padding:8px 12px">Klient</th>
            <th style="padding:8px 12px;text-align:right">Cena netto</th>
            <th style="padding:8px 12px;text-align:right">Koszt mat.</th>
            <th style="padding:8px 12px;text-align:right">Koszt robocizny</th>
            <th style="padding:8px 12px;text-align:right">Marża</th>
          </tr>
        </thead>
        <tbody>
          <tr v-for="r in rentownoscData" :key="r.order_id" style="border-bottom:1px solid var(--surface2)">
            <td style="padding:8px 12px;font-weight:600">{{ r.order_number || '#' + r.order_id }}</td>
            <td style="padding:8px 12px">{{ r.client }}</td>
            <td style="padding:8px 12px;text-align:right">{{ r.total_net ? r.total_net.toFixed(0) + ' zł' : '—' }}</td>
            <td style="padding:8px 12px;text-align:right">{{ r.material_cost ? r.material_cost.toFixed(0) + ' zł' : '—' }}</td>
            <td style="padding:8px 12px;text-align:right">{{ r.labor_cost ? r.labor_cost.toFixed(0) + ' zł' : '—' }}</td>
            <td style="padding:8px 12px;text-align:right"
                :style="r.margin_pct != null ? (r.margin_pct < 0 ? 'color:var(--rcm-red);font-weight:700' : r.margin_pct > 0.3 ? 'color:var(--rcm-green);font-weight:700' : '') : ''">
              {{ r.margin_pct != null ? (r.margin_pct * 100).toFixed(1) + '%' : '—' }}
            </td>
          </tr>
        </tbody>
      </table>
    </div>
  </div>
</template>
