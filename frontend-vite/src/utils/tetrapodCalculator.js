export const BASKETS_PER_TETRAPOD = 4

export const TETRAPOD_PARTS = Object.freeze([
  { key: 'ringSmall', name: 'Krąg mały', diameterMm: 6, lengthMm: 1220, quantityPerBasket: 1, unitWeightKg: 0.271 },
  { key: 'ringMedium', name: 'Krąg średni', diameterMm: 6, lengthMm: 1400, quantityPerBasket: 1, unitWeightKg: 0.311 },
  { key: 'ringLarge', name: 'Krąg duży', diameterMm: 6, lengthMm: 1620, quantityPerBasket: 1, unitWeightKg: 0.360 },
  { key: 'ear12', name: 'Ucho', diameterMm: 12, lengthMm: 920, quantityPerBasket: 1, unitWeightKg: 0.817, dimensions: '500×360' },
  { key: 'rod16', name: 'Pręt', diameterMm: 16, lengthMm: 1110, quantityPerBasket: 7, unitWeightKg: 1.752 },
].map(Object.freeze))

const roundKg = value => Math.round((value + Number.EPSILON) * 1000) / 1000
const roundM = value => Math.round((value + Number.EPSILON) * 100) / 100
const STEEL_DIAMETERS = [6, 12, 16]

export function createLocalId(cryptoApi = globalThis.crypto) {
  const values = cryptoApi.getRandomValues(new Uint32Array(4))
  return [...values].map(value => value.toString(16).padStart(8, '0')).join('-')
}

export function sumSteelDeliveries(deliveries) {
  if (!Array.isArray(deliveries)) {
    throw new RangeError('Historia dostaw musi być listą.')
  }
  if (!deliveries.length) return {}

  const totals = { 6: 0, 12: 0, 16: 0 }
  deliveries.forEach(delivery => {
    const weights = STEEL_DIAMETERS.map(diameter => delivery[`kg${diameter}`] ?? 0)
    if (weights.some(weight => !Number.isFinite(weight) || weight < 0) || !weights.some(weight => weight > 0)) {
      throw new RangeError('Dostawa musi zawierać dodatnią masę stali.')
    }
    STEEL_DIAMETERS.forEach((diameter, index) => {
      totals[diameter] += weights[index]
    })
  })

  return Object.fromEntries(
    Object.entries(totals).map(([diameter, weight]) => [diameter, roundKg(weight)]),
  )
}

function validateCounts(planned, completed) {
  if (!Number.isInteger(planned) || planned < 0 || !Number.isInteger(completed) || completed < 0) {
    throw new RangeError('Liczby tetrapodów muszą być pełne i nieujemne.')
  }
  if (completed > planned) {
    throw new RangeError('Wykonana liczba nie może przekraczać planu.')
  }
}

export function calculateTetrapodPlan(planned, completed, deliveriesKg = {}) {
  validateCounts(planned, completed)

  const tetrapods = { planned, completed, remaining: planned - completed }
  const baskets = Object.fromEntries(
    Object.entries(tetrapods).map(([state, count]) => [state, count * BASKETS_PER_TETRAPOD]),
  )
  const parts = TETRAPOD_PARTS.map(part => {
    const quantities = {
      perBasket: part.quantityPerBasket,
      perTetrapod: part.quantityPerBasket * BASKETS_PER_TETRAPOD,
      planned: part.quantityPerBasket * baskets.planned,
      completed: part.quantityPerBasket * baskets.completed,
      remaining: part.quantityPerBasket * baskets.remaining,
    }
    const kgPerBasket = roundKg(part.unitWeightKg * part.quantityPerBasket)
    const weightsKg = {
      perBasket: kgPerBasket,
      perTetrapod: roundKg(kgPerBasket * BASKETS_PER_TETRAPOD),
      planned: roundKg(kgPerBasket * baskets.planned),
      completed: roundKg(kgPerBasket * baskets.completed),
      remaining: roundKg(kgPerBasket * baskets.remaining),
    }
    return { ...part, quantities, weightsKg }
  })

  const perBasket = roundKg(parts.reduce((sum, part) => sum + part.weightsKg.perBasket, 0))
  const weightsKg = {
    perBasket,
    perTetrapod: roundKg(perBasket * BASKETS_PER_TETRAPOD),
    planned: roundKg(perBasket * baskets.planned),
    completed: roundKg(perBasket * baskets.completed),
    remaining: roundKg(perBasket * baskets.remaining),
  }
  const diameters = STEEL_DIAMETERS.map(diameterMm => {
    const diameterParts = TETRAPOD_PARTS.filter(part => part.diameterMm === diameterMm)
    const totalsFor = basketCount => ({
      quantity: diameterParts.reduce(
        (sum, part) => sum + part.quantityPerBasket * basketCount,
        0,
      ),
      lengthM: roundM(diameterParts.reduce(
        (sum, part) => sum + part.lengthMm * part.quantityPerBasket * basketCount,
        0,
      ) / 1000),
      weightKg: roundKg(diameterParts.reduce(
        (sum, part) => sum + part.unitWeightKg * part.quantityPerBasket * basketCount,
        0,
      )),
    })
    const deliveryValue = deliveriesKg[diameterMm]
    const deliveryKg = deliveryValue === '' || deliveryValue == null ? null : deliveryValue
    if (deliveryKg !== null && (!Number.isFinite(deliveryKg) || deliveryKg < 0)) {
      throw new RangeError('Masa dostawy musi być nieujemną liczbą.')
    }
    const planned = totalsFor(baskets.planned)
    const completed = totalsFor(baskets.completed)
    const remaining = totalsFor(baskets.remaining)
    return {
      diameterMm,
      planned,
      completed,
      remaining,
      deliveryKg,
      consumedKg: deliveryKg === null ? null : completed.weightKg,
      availableKg: deliveryKg === null ? null : roundKg(deliveryKg - completed.weightKg),
      balanceKg: deliveryKg === null ? null : roundKg(deliveryKg - planned.weightKg),
    }
  })

  return { tetrapods, baskets, weightsKg, parts, diameters }
}
