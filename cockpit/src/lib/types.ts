export interface Towar {
  id: number
  symbol: string
  nazwa: string
  jednostka: string
  cenaWyd: number
}

export interface Kontrahent {
  id: number
  nazwaSkrocona: string
  nip?: string
  telefon?: string
  aktywny: boolean
}

export interface Magazyn {
  id: number
  symbol: string
  nazwa: string
  opis?: string
}

export interface Stan {
  asortyment_id: number
  symbol: string
  magazyn_id: number
  magazyn_symbol: string
  ilosc_dostepna: number
}

export interface KontrahentUpsertRequest {
  nazwaSkrocona: string
  nip?: string
  telefon?: string
  aktywny: boolean
  typ: 'firma' | 'osoba'
}

export interface FakturaLineItem {
  symbol: string
  ilosc: number
  cena: number
}

export interface FakturaRequest {
  kontrahentId: number
  towary: FakturaLineItem[]
  dokTyp: 'faktura' | 'paragon'
  dataSprz: string
}

export interface InvoiceStatus {
  invoiceId: string
  status: string
  ksef?: {
    submitted: boolean
    status: string
  }
  createdAt: string
}
