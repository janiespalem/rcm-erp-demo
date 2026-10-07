import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import test from 'node:test'
import vm from 'node:vm'

const html = readFileSync(
  new URL('../../public/calculators/lego.html', import.meta.url),
  'utf8',
)
const start = html.indexOf('const SERIES=')
const end = html.indexOf('\n\nfunction push', start)
const context = {}

vm.runInNewContext(
  `${html.slice(start, end)}\n;globalThis.core={SERIES,makeCourse}`,
  context,
)

const { SERIES, makeCourse } = context.core
const plain = value => JSON.parse(JSON.stringify(value))

test('LEGO 40x40 does not select lengths absent from the current price list', () => {
  assert.deepEqual(plain(makeCourse(40, SERIES.lego40.blocks)), [])
  assert.deepEqual(plain(makeCourse(80, SERIES.lego40.blocks)), [])
  assert.deepEqual(plain(makeCourse(120, SERIES.lego40.blocks)), [120])
  assert.deepEqual(plain(makeCourse(200, SERIES.lego40.blocks)), [])
})

test('LEGO 60 can select the listed 90 cm block', () => {
  assert.deepEqual(plain(makeCourse(90, SERIES.lego60std.blocks)), [90])
  assert.deepEqual(plain(makeCourse(90, SERIES.lego60full.blocks)), [90])
})
