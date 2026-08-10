<script setup>
import { ref, onMounted } from 'vue'
import { api } from '@/composables/useApi'
import { isOverdue, STATUS_PL } from '@/utils/format'

const harmonogram = ref([])
onMounted(async () => { harmonogram.value = await api('/harmonogram') })
</script>

<template>
  <div class="card">
    <h2>Harmonogram / Lista zleceń</h2>
    <table>
      <thead>
        <tr><th>Nr</th><th>Klient</th><th>Status</th><th>Termin</th><th>Gałąź</th></tr>
      </thead>
      <tbody>
        <tr v-for="o in harmonogram" :key="o.id">
          <td>{{ o.order_number || '#' + o.id }}</td>
          <td>{{ o.client }}</td>
          <td><span :class="'badge badge-' + (o.status || 'draft')">{{ STATUS_PL[o.status] || o.status }}</span></td>
          <td :style="isOverdue(o.deadline) ? 'color:var(--rcm-red);font-weight:700' : ''">{{ o.deadline }}</td>
          <td><span v-if="o.branch" :class="'badge badge-' + o.branch">{{ o.branch }}</span></td>
        </tr>
      </tbody>
    </table>
  </div>
</template>
