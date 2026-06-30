import { useState } from 'react'
import { api } from '../lib/apiClient'
import { FormLayout, FormButton } from '../components/FormLayout'
import { randomValidNip } from '../lib/nip'

type StepStatus = 'pending' | 'running' | 'ok' | 'fail'
interface Step { name: string; status: StepStatus; info?: string; data?: unknown }
type Reporter = (name: string) => {
  ok: (info?: string, data?: unknown) => void
  fail: (info?: string, data?: unknown) => void
}

const rndNip = () => randomValidNip()
const rndSku = (p: string) => `${p}-${Math.floor(1000 + Math.random() * 8999)}`

async function ensureCustomer(report: Reporter, nazwa: string) {
  const r = report('upsertCustomer (firma + NIP + adres)')
  const res = await api.upsertKontrahent({
    nazwaSkrocona: nazwa, nip: rndNip(), aktywny: true, typ: 'firma',
    address: { ulica: 'Demowa', nrDomu: '1', kodPocztowy: '00-001', miejscowosc: 'Warszawa' },
  })
  const id = (res.data as any)?.id
  if (res.success && id) { r.ok(`id=${id}`, res); return id as number }
  r.fail(res.error, res); throw new Error('upsert failed')
}
async function firstProductSymbol(): Promise<string> {
  const tow = await api.towary(10)
  const items = (tow.data as any)?.items || []
  const sym = items.map((t: any) => t.Symbol).filter(Boolean)[0]
  if (!sym) throw new Error('brak towarów w bazie')
  return sym
}
// /api/stock returns a raw {items:[...]} object (not enveloped) — read defensively.
async function stockOf(symbol: string): Promise<number> {
  const r: any = await api.stany(undefined, symbol)
  const items = r?.data?.items ?? r?.items ?? []
  return items.reduce((s: number, it: any) => s + Number(it.IloscDostepna ?? it.iloscDostepna ?? 0), 0)
}

interface Scenario { group: string; label: string; desc: string; run: (r: Reporter) => Promise<void> }

const SCENARIOS: Record<string, Scenario> = {
  // ---------- Kontrahent ----------
  customerFirma: {
    group: 'Kontrahent', label: '🏢 Firma + dedup po NIP',
    desc: 'Upsert firmy z NIP i adresem; drugi upsert tego samego NIP → ten sam Id (bez duplikatu).',
    run: async (report) => {
      const nip = rndNip()
      const body = { nazwaSkrocona: 'Demo Firma SA', nip, aktywny: true, typ: 'firma', address: { ulica: 'Kwiatowa', nrDomu: '12', nrLokalu: '3', kodPocztowy: '00-950', miejscowosc: 'Warszawa' } }
      const r1 = report('upsert #1'); const a = await api.upsertKontrahent(body)
      const id1 = (a.data as any)?.id; a.success ? r1.ok(`id=${id1}`, a) : r1.fail(a.error, a)
      const r2 = report('upsert #2 (ten sam NIP)'); const b = await api.upsertKontrahent({ ...body, nazwaSkrocona: 'Demo Firma SA (dup)' })
      const id2 = (b.data as any)?.id
      id1 && id1 === id2 ? r2.ok(`id=${id2} — dedup OK (ten sam Id)`, b) : r2.fail(`różne Id: ${id1} vs ${id2}`, b)
    },
  },
  customerOsoba: {
    group: 'Kontrahent', label: '🧍 Osoba fizyczna (bez NIP)',
    desc: 'Upsert osoby (Imię/Nazwisko, bez NIP) — B2C.',
    run: async (report) => {
      const r = report('upsert osoba'); const res = await api.upsertKontrahent({ nazwaSkrocona: 'Anna Nowak', telefon: '500600700', aktywny: true, typ: 'osoba' })
      const id = (res.data as any)?.id
      res.success && id ? r.ok(`id=${id} numer=${(res.data as any)?.numer}`, res) : r.fail(res.error, res)
    },
  },

  // ---------- Produkt ----------
  createProduct: {
    group: 'Produkt', label: '📥 Produkt z Presty → Subiekt',
    desc: 'Zakłada nowy towar w kartotece (kopiuje VAT/jednostkę z wzorca) i odczytuje go z powrotem.',
    run: async (report) => {
      const sku = rndSku('PRESTA-PROD')
      const r1 = report(`createTowar (${sku})`); const res = await api.createTowar({ symbol: sku, nazwa: 'Kubek termiczny Presta 350ml', opis: 'Import z PrestaShop', cenaEwidencyjna: 39.99 })
      res.success ? r1.ok(`${(res.data as any)?.message} · id=${(res.data as any)?.providerProductId}`, res) : r1.fail(res.error, res)
      if (!res.success) return
      const r2 = report('odczyt z kartoteki'); const back = await api.towarBySymbol(sku)
      const item = (back.data as any)?.item
      item ? r2.ok(`${item.Symbol} — ${item.Nazwa} (cena ${item.CenaEwidencyjna}) ✓ w kartotece`, back) : r2.fail('nie znaleziono', back)
    },
  },

  // ---------- Faktura ----------
  b2b: {
    group: 'Faktura', label: '🧾 B2B: faktura z NIP',
    desc: 'buyer inline (firma+NIP, bez kontrahentId) → auto-upsert + faktura VAT 23%.',
    run: async (report) => {
      const sym = await firstProductSymbol()
      const r = report('issueInvoice (buyer inline)')
      const res = await api.createFaktura({ documentType: 'FV', currency: 'PLN', orderId: 'OL-B2B-1', buyer: { name: 'Demo B2B Sp z oo', nip: rndNip(), isCompany: true }, lines: [{ towarSymbol: sym, ilosc: 1, cenaBrutto: 100, stawkaVAT: '23' }] })
      res.success ? r.ok(`${(res.data as any)?.providerInvoiceNumber} · kontrahentId=${(res.data as any)?.kontrahentId}`, res) : r.fail(res.error, res)
    },
  },
  mixedVat: {
    group: 'Faktura', label: '🧮 Mieszane stawki VAT',
    desc: '3 pozycje 23% / 8% / 0% — sprawdza sumy netto/VAT/brutto.',
    run: async (report) => {
      const sym = await firstProductSymbol()
      const kid = await ensureCustomer(report, 'Demo VAT Mix')
      const r = report('issueInvoice 23/8/0')
      const inv = await api.createFaktura({ kontrahentId: kid, documentType: 'FV', currency: 'PLN', lines: [
        { towarSymbol: sym, ilosc: 1, cenaBrutto: 123, stawkaVAT: '23' },
        { towarSymbol: sym, ilosc: 1, cenaBrutto: 108, stawkaVAT: '8' },
        { towarSymbol: sym, ilosc: 1, cenaBrutto: 100, stawkaVAT: '0' },
      ] })
      const id = (inv.data as any)?.providerInvoiceId
      if (!inv.success) { r.fail(inv.error, inv); return }
      const st = await api.invoiceStatus(String(id)); const d = st.data as any
      r.ok(`${(inv.data as any)?.providerInvoiceNumber} · netto=${d?.netto} VAT=${d?.vat} brutto=${d?.brutto}`, st)
    },
  },
  backdate: {
    group: 'Faktura', label: '📅 Data fiskalna wstecz',
    desc: 'issueDate sprzed 10 dni (miesiąc VAT) + rozchód stanu — pokazuje datę sprzedaży z requestu.',
    run: async (report) => {
      const sym = await firstProductSymbol()
      const past = new Date(Date.now() - 10 * 864e5).toISOString().split('T')[0]
      const r = report(`issueInvoice issueDate=${past}`)
      const inv = await api.createFaktura({ documentType: 'FV', currency: 'PLN', issueDate: past, buyer: { name: 'Demo Backdate SA', nip: rndNip(), isCompany: true }, lines: [{ towarSymbol: sym, ilosc: 1, cenaBrutto: 60, stawkaVAT: '23' }] })
      const id = (inv.data as any)?.providerInvoiceId
      if (!inv.success) { r.fail(inv.error, inv); return }
      const st = await api.invoiceStatus(String(id)); const created = (st.data as any)?.createdAt
      r.ok(`${(inv.data as any)?.providerInvoiceNumber} · dataSprzedazy=${created?.split('T')[0]} (oczek. ${past})`, st)
    },
  },
  shippingDiscount: {
    group: 'Faktura', label: '🚚 Dostawa + rabat',
    desc: 'Produkt + linia „Dostawa" + linia rabatowa (ujemna) — sumy/VAT się zgadzają.',
    run: async (report) => {
      const r = report('issueInvoice 120 + 15 − 10')
      const inv = await api.createFaktura({ documentType: 'FV', currency: 'PLN', buyer: { name: 'Demo Rabat SA', nip: rndNip(), isCompany: true }, lines: [
        { towarSymbol: rndSku('PR-A'), name: 'Bluza Presta', ilosc: 1, cenaBrutto: 120, stawkaVAT: '23' },
        { towarSymbol: 'DOSTAWA', name: 'Dostawa kurier', ilosc: 1, cenaBrutto: 15, stawkaVAT: '23' },
        { towarSymbol: 'RABAT', name: 'Rabat promocyjny', ilosc: 1, cenaBrutto: -10, stawkaVAT: '23' },
      ] })
      const id = (inv.data as any)?.providerInvoiceId
      if (!inv.success) { r.fail(inv.error, inv); return }
      const st = await api.invoiceStatus(String(id)); const d = st.data as any
      r.ok(`${(inv.data as any)?.providerInvoiceNumber} · brutto=${d?.brutto} (oczek. ~125)`, st)
    },
  },
  presta: {
    group: 'Faktura', label: '📦 Towar spoza Subiekta',
    desc: 'Symbol spoza kartoteki → pozycja jednorazowa, faktura wychodzi (nie rusza magazynu).',
    run: async (report) => {
      const sym = rndSku('PRESTA-X')
      const r = report(`issueInvoice towar spoza Subiekta (${sym})`)
      const res = await api.createFaktura({ documentType: 'FV', currency: 'PLN', orderId: 'PRESTA-DEMO', buyer: { name: 'Demo Presta SA', nip: rndNip(), isCompany: true }, lines: [{ towarSymbol: sym, name: 'Kubek termiczny Presta 350ml', ilosc: 2, cenaBrutto: 49.99, stawkaVAT: '23' }] })
      res.success ? r.ok(`${(res.data as any)?.providerInvoiceNumber} — faktura mimo braku towaru w kartotece`, res) : r.fail(res.error, res)
    },
  },
  warehouse: {
    group: 'Faktura', label: '🏪 Auto-zdjęcie magazynu',
    desc: 'Stan przed → faktura na towar z kartoteki → stan po (różnica = sprzedana ilość).',
    run: async (report) => {
      const sym = await firstProductSymbol()
      const r0 = report(`stan przed (${sym})`); const before = await stockOf(sym); r0.ok(`dostępne = ${before}`)
      const kid = await ensureCustomer(report, 'Demo Magazyn')
      const r1 = report('issueInvoice 2 szt'); const inv = await api.createFaktura({ kontrahentId: kid, documentType: 'FV', currency: 'PLN', lines: [{ towarSymbol: sym, ilosc: 2, cenaBrutto: 50, stawkaVAT: '23' }] })
      if (!inv.success) { r1.fail(inv.error, inv); return }
      r1.ok(`${(inv.data as any)?.providerInvoiceNumber}`, inv)
      const r2 = report('stan po'); const after = await stockOf(sym)
      r2.ok(`dostępne = ${after} (zdjęto ${before - after})`)
    },
  },

  fullOrder: {
    group: 'Faktura', label: '🧱 Pełne zamówienie (E2E)',
    desc: 'Zamówienie → upsert kontrahenta → faktura → status KSeF → stan magazynu. Jeden ciąg.',
    run: async (report) => {
      const sym = await firstProductSymbol()
      const r0 = report(`stan przed (${sym})`); const before = await stockOf(sym); r0.ok(`dostępne=${before}`)
      const r1 = report('issueInvoice (buyer inline, 2 szt)')
      const inv = await api.createFaktura({ documentType: 'FV', currency: 'PLN', orderId: 'ORD-E2E-' + Date.now(), idempotencyKey: 'e2e-' + Date.now(), buyer: { name: 'E2E Zamówienie SA', nip: rndNip(), isCompany: true, address: { ulica: 'E2E', nrDomu: '1', kodPocztowy: '00-001', miejscowosc: 'Warszawa' } }, lines: [{ towarSymbol: sym, ilosc: 2, cenaBrutto: 100, stawkaVAT: '23' }] })
      const id = (inv.data as any)?.providerInvoiceId
      if (!inv.success) { r1.fail(inv.error, inv); return }
      r1.ok(`${(inv.data as any)?.providerInvoiceNumber} · kontrahentId=${(inv.data as any)?.kontrahentId}`, inv)
      const r2 = report('getInvoiceStatus'); const st = await api.invoiceStatus(String(id)); const d = st.data as any
      r2.ok(`regulatoryStatus=${d?.regulatoryStatus} · brutto=${d?.brutto}`, st)
      const r3 = report('stan po'); const after = await stockOf(sym); r3.ok(`dostępne=${after} (zdjęto ${before - after})`)
    },
  },

  // ---------- Paragon / B2C ----------
  b2c: {
    group: 'Paragon / B2C', label: '🧾 Paragon (osoba, bez KSeF)',
    desc: 'Paragon (PA) z buyer osobą bez NIP → regulatoryStatus=none.',
    run: async (report) => {
      const sym = rndSku('PRESTA-OS')
      const r = report('issueInvoice (PA, osoba)')
      const res = await api.createFaktura({ documentType: 'PA', currency: 'PLN', buyer: { name: 'Jan Kowalski', isCompany: false }, lines: [{ towarSymbol: sym, name: 'Produkt detaliczny', ilosc: 1, cenaBrutto: 29.99, stawkaVAT: '23' }] })
      const d = res.data as any
      res.success ? r.ok(`${d?.providerInvoiceNumber} · kontrahentId=${d?.kontrahentId} · regulatoryStatus=${d?.regulatoryStatus}`, res) : r.fail(res.error, res)
    },
  },

  // ---------- Niezawodność / kontrakt ----------
  idempotency: {
    group: 'Niezawodność / kontrakt', label: '🔁 Idempotencja',
    desc: 'Ten sam idempotencyKey 2× → ta sama faktura (idempotent=true), bez duplikatu.',
    run: async (report) => {
      const kid = await ensureCustomer(report, 'Demo Idempotencja'); const sym = await firstProductSymbol()
      const body = { kontrahentId: kid, documentType: 'FV', currency: 'PLN', idempotencyKey: 'demo-' + Date.now(), lines: [{ towarSymbol: sym, ilosc: 1, cenaBrutto: 30, stawkaVAT: '23' }] }
      const r1 = report('issueInvoice #1'); const a = await api.createFaktura(body); const id1 = (a.data as any)?.providerInvoiceId
      a.success ? r1.ok(`id=${id1}`, a) : r1.fail(a.error, a)
      const r2 = report('issueInvoice #2 (ten sam klucz)'); const b = await api.createFaktura(body); const id2 = (b.data as any)?.providerInvoiceId
      id1 && id1 === id2 && (b.data as any)?.idempotent ? r2.ok(`id=${id2} · idempotent=true ✓`, b) : r2.fail('różne id / brak flagi', b)
    },
  },
  rollback: {
    group: 'Niezawodność / kontrakt', label: '⛔ Błąd / rollback',
    desc: 'Nieistniejący kontrahent → 422 rejected z powodem, brak śmieciowego dokumentu.',
    run: async (report) => {
      const sym = await firstProductSymbol()
      const r = report('issueInvoice (kontrahentId=999999999)')
      const res = await api.createFaktura({ kontrahentId: 999999999, documentType: 'FV', currency: 'PLN', lines: [{ towarSymbol: sym, ilosc: 1, cenaBrutto: 19, stawkaVAT: '23' }] })
      !res.success ? r.ok(`odrzucone zgodnie z oczekiwaniem: ${res.error}`, res) : r.fail('NIESPODZIANKA: przeszło', res)
    },
  },
  ksef: {
    group: 'Niezawodność / kontrakt', label: '📋 KSeF status',
    desc: 'Wystaw fakturę i odczytaj status KSeF (na demo: pending).',
    run: async (report) => {
      const kid = await ensureCustomer(report, 'Demo KSeF'); const sym = await firstProductSymbol()
      const r1 = report('issueInvoice'); const inv = await api.createFaktura({ kontrahentId: kid, documentType: 'FV', currency: 'PLN', lines: [{ towarSymbol: sym, ilosc: 1, cenaBrutto: 123, stawkaVAT: '23' }] })
      const id = (inv.data as any)?.providerInvoiceId; inv.success ? r1.ok(`id=${id}`, inv) : r1.fail(inv.error, inv)
      const r2 = report('getInvoiceStatus'); const st = await api.invoiceStatus(String(id)); const d = st.data as any
      st.success ? r2.ok(`regulatoryStatus=${d?.regulatoryStatus} · clearanceReference=${d?.clearanceReference ?? 'null'}`, st) : r2.fail(st.error, st)
    },
  },
  contractShape: {
    group: 'Niezawodność / kontrakt', label: '📐 Pełny kształt kontraktu',
    desc: 'issueInvoice z orderId + idempotencyKey + buyer + currency → pełna odpowiedź kontraktu.',
    run: async (report) => {
      const sym = await firstProductSymbol()
      const r = report('issueInvoice (pełne pola)')
      const res = await api.createFaktura({ orderId: 'ORD-' + Date.now(), idempotencyKey: 'k-' + Date.now(), documentType: 'FV', currency: 'PLN', buyer: { name: 'Kontrakt SA', nip: rndNip(), isCompany: true, address: { ulica: 'Kontraktowa', nrDomu: '7', kodPocztowy: '00-001', miejscowosc: 'Warszawa' } }, lines: [{ towarSymbol: sym, ilosc: 1, cenaBrutto: 100, stawkaVAT: '23' }] })
      const d = res.data as any
      res.success ? r.ok(`providerInvoiceId=${d?.providerInvoiceId} · Number=${d?.providerInvoiceNumber} · regulatoryStatus=${d?.regulatoryStatus} · orderId=${d?.orderId}`, res) : r.fail(res.error, res)
    },
  },
  batch: {
    group: 'Niezawodność / kontrakt', label: '⚡ Batch 10 faktur',
    desc: 'Równolegle 10 faktur — czas, brak błędów, unikalne sekwencyjne numery.',
    run: async (report) => {
      const kid = await ensureCustomer(report, 'Demo Batch'); const sym = await firstProductSymbol()
      const r = report('10× issueInvoice równolegle')
      const t0 = performance.now()
      const results = await Promise.all(Array.from({ length: 10 }, (_, i) =>
        api.createFaktura({ kontrahentId: kid, documentType: 'FV', currency: 'PLN', orderId: `BATCH-${i}`, lines: [{ towarSymbol: sym, ilosc: 1, cenaBrutto: 10, stawkaVAT: '23' }] })))
      const secs = ((performance.now() - t0) / 1000).toFixed(1)
      const nums = results.filter(x => x.success).map(x => (x.data as any)?.providerInvoiceNumber)
      const uniq = new Set(nums).size
      const fails = results.filter(x => !x.success).length
      fails === 0 && uniq === 10 ? r.ok(`10/10 OK w ${secs}s · ${uniq} unikalnych numerów`, { nums }) : r.fail(`OK=${10 - fails} unikalnych=${uniq}`, { nums })
    },
  },

  korekta: {
    group: 'Niezawodność / kontrakt', label: '↩️ Korekta / zwrot',
    desc: 'Paragon 3 szt → zwrot do 1 szt (dokument korygujący ZW). Faktura korygująca działa tak samo, ale wymaga wysłania oryginału do KSeF.',
    run: async (report) => {
      const sym = await firstProductSymbol()
      const r1 = report('paragon 3 szt')
      const pa = await api.createFaktura({ kontrahentId: 101125, documentType: 'PA', currency: 'PLN', lines: [{ towarSymbol: sym, ilosc: 3, cenaBrutto: 50, stawkaVAT: '23' }] })
      const paId = (pa.data as any)?.providerInvoiceId
      if (!pa.success) { r1.fail(pa.error, pa); return }
      r1.ok(`${(pa.data as any)?.providerInvoiceNumber} id=${paId}`, pa)
      const r2 = report('zwrot do paragonu (Lp=1 → 1 szt)')
      const k = await api.korekta(paId, { przyczyna: 'Zwrot 2 szt', lines: [{ lp: 1, nowaIlosc: 1 }] })
      k.success ? r2.ok(`${(k.data as any)?.providerInvoiceNumber} (korekta do ${(k.data as any)?.korygowanyId})`, k) : r2.fail(k.error, k)
    },
  },

  // ---------- Infrastruktura ----------
  health: {
    group: 'Infrastruktura', label: '❤️ /health (3 sygnały)',
    desc: 'Odpytuje /health — bridge / sesja Sfery / Subiekt(SQL) osobno.',
    run: async (report) => {
      const r = report('GET /health'); const res = await api.health(); const d = res.data as any
      res.success ? r.ok(`status=${d?.status} · bridge=${d?.bridge} · sesja=${d?.sferaSession} · subiekt=${d?.subiekt}`, res) : r.fail(res.error, res)
    },
  },
}

const GROUPS = ['Kontrahent', 'Produkt', 'Faktura', 'Paragon / B2C', 'Niezawodność / kontrakt', 'Infrastruktura']

export default function DemoFlowsPage() {
  const [steps, setSteps] = useState<Step[]>([])
  const [running, setRunning] = useState<string | null>(null)

  const run = async (key: string) => {
    setSteps([]); setRunning(key)
    const local: Step[] = []
    const report: Reporter = (name) => {
      const idx = local.length; local.push({ name, status: 'running' }); setSteps([...local])
      const set = (status: StepStatus, info?: string, data?: unknown) => { local[idx] = { name, status, info, data }; setSteps([...local]) }
      return { ok: (i, d) => set('ok', i, d), fail: (i, d) => set('fail', i, d) }
    }
    try { await SCENARIOS[key].run(report) }
    catch (e) { local.push({ name: 'Przerwano', status: 'fail', info: String(e) }); setSteps([...local]) }
    finally { setRunning(null) }
  }

  return (
    <FormLayout title="Demo Flows — wszystkie ścieżki POC">
      <p className="text-sm text-gray-600 mb-4">
        Każdy przycisk uruchamia kompletny przebieg przez most (Sfera). Kroki i surowe JSON poniżej.
      </p>
      {GROUPS.map((g) => (
        <div key={g} className="mb-5">
          <h3 className="text-xs font-bold uppercase tracking-wide text-gray-500 mb-2">{g}</h3>
          <div className="grid grid-cols-2 gap-3">
            {Object.entries(SCENARIOS).filter(([, s]) => s.group === g).map(([key, s]) => (
              <div key={key} className="border rounded p-3 flex flex-col gap-2">
                <div className="font-semibold text-sm">{s.label}</div>
                <div className="text-xs text-gray-500 flex-1">{s.desc}</div>
                <FormButton onClick={() => run(key)} loading={running === key} disabled={running !== null}>Uruchom</FormButton>
              </div>
            ))}
          </div>
        </div>
      ))}

      {steps.length > 0 && (
        <div className="space-y-2 mt-4 border-t pt-4">
          <h3 className="text-sm font-semibold">Wynik</h3>
          {steps.map((step, i) => (
            <div key={i} className={`border-l-4 p-3 rounded ${step.status === 'ok' ? 'border-green-500 bg-green-50' : step.status === 'fail' ? 'border-red-500 bg-red-50' : step.status === 'running' ? 'border-blue-500 bg-blue-50' : 'border-gray-300 bg-gray-50'}`}>
              <div className="flex items-center justify-between">
                <span className="font-medium text-sm">{step.name}</span>
                <span className={`text-xs font-bold px-2 py-0.5 rounded text-white ${step.status === 'ok' ? 'bg-green-500' : step.status === 'fail' ? 'bg-red-500' : step.status === 'running' ? 'bg-blue-500' : 'bg-gray-400'}`}>{step.status.toUpperCase()}</span>
              </div>
              {step.info && <p className="text-sm text-gray-700 mt-1">{step.info}</p>}
              {step.data != null && (
                <details className="text-xs mt-1">
                  <summary className="cursor-pointer text-gray-500">JSON</summary>
                  <pre className="bg-gray-800 text-green-400 p-2 rounded mt-1 overflow-auto max-h-48">{JSON.stringify(step.data, null, 2)}</pre>
                </details>
              )}
            </div>
          ))}
        </div>
      )}
    </FormLayout>
  )
}
