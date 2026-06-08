<script setup>
import { onMounted, computed } from 'vue'
import { useNotifications } from '@/composables/useNotifications'
import { useOrders } from '@/composables/useOrders'

const { pytania, answerDraft, loadPytania, submitAnswer } = useNotifications()
const { orders } = useOrders()

onMounted(loadPytania)

function orderFor(order_id) {
  return orders.value.find(o => o.id === order_id) || null
}

function isUrgent(order) {
  if (!order?.deadline) return false
  const daysLeft = (new Date(order.deadline) - new Date()) / (1000 * 60 * 60 * 24)
  return daysLeft <= 3
}
</script>

<template>
  <div class="card">
    <h2>Pytania od Technologa</h2>
    <p v-if="pytania.length === 0" style="color:var(--muted);padding:16px 0">
      Brak oczekujących pytań ✓
    </p>
    <div v-for="p in pytania" :key="p.id" class="pytanie-card" :class="{'pytanie-pending': p.status==='pending'}">
      <div class="pytanie-meta">
        <span class="pytanie-nr">Zlecenie #{{ p.order_id }}</span>
        <template v-if="orderFor(p.order_id)">
          <span class="pytanie-client">{{ orderFor(p.order_id).client || '—' }}</span>
          <span class="pytanie-deadline" :class="{'pytanie-deadline-warn': isUrgent(orderFor(p.order_id))}">
            termin: {{ orderFor(p.order_id).deadline }}
          </span>
        </template>
        <span v-if="p.status==='pending' && isUrgent(orderFor(p.order_id))" class="badge-pilne">PILNE</span>
        <span :class="p.status === 'pending' ? 'param-badge' : 'badge badge-done'" style="margin-left:auto">
          {{ p.status === 'pending' ? '⏳ Czeka na odpowiedź' : '✓ Odpowiedziano' }}
        </span>
      </div>

      <div class="pytanie-time">{{ p.asked_at?.slice(0, 16).replace('T', ' ') }}</div>
      <div class="pytanie-text">{{ p.question_text }}</div>

      <div v-if="p.status === 'answered'" class="pytanie-answer">
        <strong>Twoja odpowiedź:</strong> {{ p.answer_text }}
      </div>
      <div v-if="p.status === 'pending'" class="pytanie-reply">
        <input v-model="answerDraft[p.id]" placeholder="Wpisz odpowiedź..."
               @keyup.enter="submitAnswer(p.id)">
        <button class="btn btn-success btn-sm" @click="submitAnswer(p.id)" :disabled="!answerDraft[p.id]">
          Wyślij
        </button>
      </div>
    </div>
  </div>
</template>

<style scoped>
.pytanie-card {
  border: 1px solid var(--border);
  border-radius: var(--radius);
  padding: 14px 16px;
  margin-bottom: 12px;
}
.pytanie-pending {
  border-left: 3px solid var(--rcm-accent);
}

.pytanie-meta {
  display: flex;
  align-items: center;
  gap: 10px;
  flex-wrap: wrap;
  margin-bottom: 6px;
}
.pytanie-nr {
  font-size: 0.8rem;
  color: var(--muted);
  font-weight: 600;
}
.pytanie-client {
  font-size: 0.82rem;
  font-weight: 600;
  color: var(--text);
}
.pytanie-deadline {
  font-size: 0.8rem;
  color: var(--muted);
}
.pytanie-deadline-warn {
  color: var(--rcm-red);
  font-weight: 700;
}

.badge-pilne {
  background: var(--rcm-red);
  color: #fff;
  border-radius: 4px;
  padding: 1px 7px;
  font-size: 0.72rem;
  font-weight: 800;
  letter-spacing: 0.05em;
}

.pytanie-time {
  font-size: 0.75rem;
  color: var(--muted);
  margin-bottom: 6px;
}
.pytanie-text {
  font-weight: 600;
  margin-bottom: 10px;
  font-size: 0.95rem;
}
.pytanie-answer {
  background: rgba(46, 125, 50, 0.1);
  padding: 8px 12px;
  border-radius: var(--radius);
  font-size: 0.88rem;
}
.pytanie-reply {
  display: flex;
  gap: 8px;
  margin-top: 6px;
}
.pytanie-reply input {
  flex: 1;
  padding: 8px 12px;
  border: 1px solid var(--border);
  border-radius: var(--radius);
  background: var(--bg);
  color: var(--text);
  font-size: 0.9rem;
}
.pytanie-reply input:focus {
  outline: none;
  border-color: var(--rcm-accent);
}
</style>
