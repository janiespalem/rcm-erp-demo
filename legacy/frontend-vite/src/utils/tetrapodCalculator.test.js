import test from 'node:test'
import assert from 'node:assert/strict'

import { calculateTetrapodPlan, createLocalId, sumSteelDeliveries } from './tetrapodCalculator.js'

test('creates an id when randomUUID is unavailable on an HTTP page', () => {
  const insecureContextCrypto = {
    getRandomValues(values) {
      values.set([0, 1, 0xffffffff, 42])
      return values
    },
  }

  assert.equal(createLocalId(insecureContextCrypto), '00000000-00000001-ffffffff-0000002a')
})

test('calculates the confirmed one-basket and one-tetrapod norm', () => {
  const result = calculateTetrapodPlan(1, 0)

  assert.equal(result.weightsKg.perBasket, 14.023)
  assert.equal(result.weightsKg.perTetrapod, 56.092)
  assert.equal(result.baskets.planned, 4)
  assert.equal(
    result.parts.find(part => part.key === 'rod16').quantities.perTetrapod,
    28,
  )
})

test('calculates planned, completed, and remaining reinforcement', () => {
  const result = calculateTetrapodPlan(10, 3)

  assert.equal(result.tetrapods.remaining, 7)
  assert.equal(result.baskets.remaining, 28)
  assert.equal(result.weightsKg.remaining, 392.644)
  assert.deepEqual(
    result.diameters.find(row => row.diameterMm === 6).remaining,
    { quantity: 84, lengthM: 118.72, weightKg: 26.376 },
  )
  assert.equal(
    result.parts.find(part => part.key === 'rod16').quantities.remaining,
    196,
  )
})

test('rejects impossible production counts', () => {
  assert.throws(() => calculateTetrapodPlan(-1, 0), RangeError)
  assert.throws(() => calculateTetrapodPlan(2.5, 0), RangeError)
  assert.throws(() => calculateTetrapodPlan(2, 3), RangeError)
})

test('consumes delivered steel for completed production before showing current stock', () => {
  const result = calculateTetrapodPlan(10, 3, { 6: 30, 12: 20, 16: 400 })

  assert.deepEqual(
    result.diameters.map(row => ({
      diameterMm: row.diameterMm,
      deliveryKg: row.deliveryKg,
      consumedKg: row.consumedKg,
      availableKg: row.availableKg,
      balanceKg: row.balanceKg,
    })),
    [
      { diameterMm: 6, deliveryKg: 30, consumedKg: 11.304, availableKg: 18.696, balanceKg: -7.68 },
      { diameterMm: 12, deliveryKg: 20, consumedKg: 9.804, availableKg: 10.196, balanceKg: -12.68 },
      { diameterMm: 16, deliveryKg: 400, consumedKg: 147.168, availableKg: 252.832, balanceKg: -90.56 },
    ],
  )
})

test('allows an omitted delivery and rejects a negative delivery weight', () => {
  const result = calculateTetrapodPlan(1, 0)

  assert.equal(result.diameters[0].deliveryKg, null)
  assert.equal(result.diameters[0].balanceKg, null)
  assert.throws(() => calculateTetrapodPlan(1, 0, { 6: -1 }), RangeError)
})

test('sums separate steel receipts before calculating the available balance', () => {
  const totals = sumSteelDeliveries([
    { kg6: 20, kg12: 10.5, kg16: 300 },
    { kg6: 10, kg12: 9.5, kg16: 100 },
  ])
  const result = calculateTetrapodPlan(10, 3, totals)

  assert.deepEqual(totals, { 6: 30, 12: 20, 16: 400 })
  assert.deepEqual(
    result.diameters.map(row => row.balanceKg),
    [-7.68, -12.68, -90.56],
  )
})

test('rejects an invalid steel receipt instead of corrupting delivery totals', () => {
  assert.throws(
    () => sumSteelDeliveries([{ kg6: 0, kg12: 0, kg16: 0 }]),
    RangeError,
  )
  assert.throws(
    () => sumSteelDeliveries([{ kg6: -1, kg12: 0, kg16: 0 }]),
    RangeError,
  )
})
