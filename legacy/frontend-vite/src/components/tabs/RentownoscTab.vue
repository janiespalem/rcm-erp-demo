<script setup>
import { ref, onMounted } from 'vue'
import { api } from '@/composables/useApi'

const rentownoscData = ref([])
async function load() { rentownoscData.value = await api('/rentownosc') }
onMounted(load)
</script>

<template>
  <div>
    <div class="tab-toolbar">
      <span></span>
      <button class="btn btn-outline btn-sm" @click="load()">Odśwież</button>
    </div>
    <div v-if="!rentownoscData.length" style="text-align:center;padding:32px;color:var(--muted)">
      Brak danych — wyceny zleceń wymagane
    </div>
    <div v-else style="overflow-x:auto">
      <table class="data-table">
        <thead>
          <tr>
            <th>Nr zlecenia</th>
            <th>Klient</th>
            <th class="num">Cena netto</th>
            <th class="num">Koszt mat.</th>
            <th class="num">Koszt robocizny</th>
            <th class="num">Marża</th>
          </tr>
        </thead>
        <tbody>
          <tr v-for="r in rentownoscData" :key="r.order_id">
            <td style="font-weight:600">{{ r.order_number || '#' + r.order_id }}</td>
            <td>{{ r.client }}</td>
            <td class="num">{{ r.total_net ? r.total_net.toFixed(0) + ' zł' : '—' }}</td>
            <td class="num">{{ r.material_cost ? r.material_cost.toFixed(0) + ' zł' : '—' }}</td>
            <td class="num">{{ r.labor_cost ? r.labor_cost.toFixed(0) + ' zł' : '—' }}</td>
            <td class="num"
                :style="r.margin_pct != null ? (r.margin_pct < 0 ? 'color:var(--rcm-red);font-weight:700' : r.margin_pct > 0.3 ? 'color:var(--rcm-green);font-weight:700' : '') : ''">
              {{ r.margin_pct != null ? (r.margin_pct * 100).toFixed(1) + '%' : '—' }}
            </td>
          </tr>
        </tbody>
      </table>
    </div>
  </div>
</template>
