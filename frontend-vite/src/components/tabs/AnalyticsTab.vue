<script setup>
import { ref, onMounted } from 'vue'
import { api, openPdf } from '@/composables/useApi'

const analytics = ref(null)

async function load() {
  analytics.value = await api('/analytics')
}

onMounted(load)
</script>

<template>
  <div>
    <div style="display:flex;justify-content:space-between;align-items:center;margin-bottom:12px">
      <h2 style="font-size:1rem;font-weight:700;color:var(--rcm-blue)">Dashboard Dyrektora</h2>
      <div style="display:flex;gap:8px">
        <button class="btn btn-outline btn-sm" @click="load()" title="Odśwież dane">🔄 Odśwież</button>
        <button class="btn btn-success btn-sm" @click="openPdf('/export/xlsx')"
                title="Pobierz wszystkie zlecenia jako plik Excel">
          Pobierz XLSX
        </button>
      </div>
    </div>

    <div class="stat-grid" style="margin-bottom:16px" v-if="analytics">
      <div class="stat-card">
        <div class="num">{{ analytics.total_orders }}</div>
        <div class="lbl">Łącznie zleceń</div>
      </div>
      <div class="stat-card accent-red">
        <div class="num" style="color:var(--rcm-red)">{{ analytics.odrzut_count }}</div>
        <div class="lbl">Odrzuty ({{ analytics.odrzut_pct }}%)</div>
      </div>
      <div class="stat-card accent-green">
        <div class="num" style="color:var(--rcm-green)">{{ analytics.standard_count }}</div>
        <div class="lbl">Standard</div>
      </div>
      <div class="stat-card accent-warn">
        <div class="num" style="color:var(--rcm-warn)">{{ analytics.niestandard_count }}</div>
        <div class="lbl">Niestandard</div>
      </div>
      <div class="stat-card">
        <div class="num" style="color:#1565c0">{{ analytics.orders_in_production }}</div>
        <div class="lbl">W realizacji</div>
      </div>
      <div class="stat-card accent-purple">
        <div class="num" style="color:#6a1b9a">{{ analytics.orders_done }}</div>
        <div class="lbl">Zakończone</div>
      </div>
      <div class="stat-card" v-if="analytics.avg_cycle_days != null">
        <div class="num" style="color:#00695c">{{ analytics.avg_cycle_days }}</div>
        <div class="lbl">Śr. dni cyklu</div>
      </div>
      <div class="stat-card" v-if="analytics.avg_quote_to_start_days != null">
        <div class="num" style="color:#e65100">{{ analytics.avg_quote_to_start_days }}</div>
        <div class="lbl">Śr. dni wycena→start</div>
      </div>
      <div class="stat-card" v-if="analytics.estimate_accuracy_pct != null">
        <div class="num" :style="analytics.estimate_accuracy_pct > 120 ? 'color:var(--rcm-red)' : analytics.estimate_accuracy_pct < 80 ? 'color:var(--rcm-green)' : 'color:var(--rcm-blue)'">
          {{ analytics.estimate_accuracy_pct }}%
        </div>
        <div class="lbl">Trafność wyceny (h)</div>
      </div>
    </div>

    <div style="display:grid;grid-template-columns:1fr 1fr;gap:16px;margin-bottom:16px" v-if="analytics">
      <div class="card" v-if="analytics.avg_margin_pct">
        <h2>Średnia marża</h2>
        <p style="font-size:2rem;font-weight:800;color:var(--rcm-blue)">{{ analytics.avg_margin_pct }}%</p>
      </div>
      <div class="card" :style="analytics.overdue_orders?.length ? 'border-top:3px solid var(--rcm-red)' : ''">
        <h2>⚠ Przeterminowane ({{ analytics.overdue_orders?.length || 0 }})</h2>
        <div v-if="!analytics.overdue_orders?.length" style="color:var(--muted);font-size:0.85rem">
          Brak przeterminowanych ✓
        </div>
        <table v-else style="font-size:0.82rem;margin-top:8px">
          <thead><tr><th>Nr</th><th>Klient</th><th>Status</th><th>Termin</th></tr></thead>
          <tbody>
            <tr v-for="o in analytics.overdue_orders" :key="o.id" style="color:var(--rcm-red)">
              <td>{{ o.order_number }}</td>
              <td>{{ o.client }}</td>
              <td><span :class="'badge badge-'+o.status">{{ o.status }}</span></td>
              <td style="font-weight:700">{{ o.deadline }}</td>
            </tr>
          </tbody>
        </table>
      </div>
    </div>

    <div class="card" v-if="analytics?.revenue_by_month?.length" style="margin-bottom:16px">
      <h2>Przychód — ostatnie 6 miesięcy</h2>
      <table style="margin-top:8px">
        <thead><tr><th>Miesiąc</th><th>Zlecenia</th><th>Przychód netto (PLN)</th></tr></thead>
        <tbody>
          <tr v-for="r in analytics.revenue_by_month" :key="r.month">
            <td>{{ r.month }}</td>
            <td>{{ r.orders }}</td>
            <td style="font-weight:700;color:var(--rcm-blue)">
              {{ r.revenue_pln.toLocaleString('pl-PL', {minimumFractionDigits:2}) }}
            </td>
          </tr>
        </tbody>
      </table>
    </div>
    <div class="card" v-else-if="analytics && !analytics.revenue_by_month?.length" style="margin-bottom:16px">
      <h2>Przychód — ostatnie 6 miesięcy</h2>
      <p style="color:var(--muted);font-size:0.85rem">Brak zatwierdzonych zleceń z wyceną.</p>
    </div>

    <div class="card" v-if="analytics?.top_clients?.length">
      <h2>Top klientów</h2>
      <table style="margin-top:8px">
        <thead><tr><th>#</th><th>Klient</th><th>Zlecenia</th><th>Przychód netto (PLN)</th></tr></thead>
        <tbody>
          <tr v-for="(c, i) in analytics.top_clients" :key="c.client">
            <td style="color:var(--muted);font-weight:700">{{ i+1 }}</td>
            <td style="font-weight:600">{{ c.client }}</td>
            <td>{{ c.orders }}</td>
            <td style="color:var(--rcm-blue);font-weight:700">
              {{ c.revenue_pln > 0 ? c.revenue_pln.toLocaleString('pl-PL', {minimumFractionDigits:2}) : '—' }}
            </td>
          </tr>
        </tbody>
      </table>
    </div>
  </div>
</template>
