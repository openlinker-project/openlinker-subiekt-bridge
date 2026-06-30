import { useState } from 'react'
import ConnectPage from './pages/Connect'
import TowyPage from './pages/Towary'
import KontrahenciPage from './pages/Kontrahenci'
import KontrahentUpsertPage from './pages/KontrahentUpsert'
import FakturaPage from './pages/Faktura'
import KsefStatusPage from './pages/KsefStatus'
import FlowPage from './pages/Flow'
import AuditPage from './pages/Audit'
import P0TestFlowPage from './pages/P0TestFlow'
import DemoFlowsPage from './pages/DemoFlows'

type TabId = 'connect' | 'towary' | 'kontrahenci' | 'kontrahent-upsert' | 'faktura' | 'ksef-status' | 'flow' | 'audit' | 'p0-test' | 'demo-flows'

const tabs = [
  { id: 'connect' as TabId, label: '🔌 Connect' },
  { id: 'towary' as TabId, label: '📦 Towary' },
  { id: 'kontrahenci' as TabId, label: '👥 Kontrahenci' },
  { id: 'kontrahent-upsert' as TabId, label: '➕ Upsert Kontrahent' },
  { id: 'faktura' as TabId, label: '📄 Faktura' },
  { id: 'ksef-status' as TabId, label: '📋 KSeF Status' },
  { id: 'flow' as TabId, label: '🔄 E2E Flow' },
  { id: 'demo-flows' as TabId, label: '🎬 Demo Flows' },
  { id: 'p0-test' as TabId, label: '✅ P0 Test' },
  { id: 'audit' as TabId, label: '📊 Audit' },
]

export default function App() {
  const [activeTab, setActiveTab] = useState<TabId>('connect')

  const renderTab = () => {
    switch (activeTab) {
      case 'connect':
        return <ConnectPage />
      case 'towary':
        return <TowyPage />
      case 'kontrahenci':
        return <KontrahenciPage />
      case 'kontrahent-upsert':
        return <KontrahentUpsertPage />
      case 'faktura':
        return <FakturaPage />
      case 'ksef-status':
        return <KsefStatusPage />
      case 'flow':
        return <FlowPage />
      case 'demo-flows':
        return <DemoFlowsPage />
      case 'p0-test':
        return <P0TestFlowPage />
      case 'audit':
        return <AuditPage />
    }
  }

  return (
    <div className="min-h-screen bg-gray-50">
      {/* Header */}
      <header className="bg-white shadow">
        <div className="max-w-7xl mx-auto px-4 py-4">
          <h1 className="text-3xl font-bold text-gray-900">
            Subiekt POC Cockpit
          </h1>
          <p className="text-gray-600 text-sm mt-1">
            Testing Sfera SDK integration via bridge on localhost:5005
          </p>
        </div>
      </header>

      {/* Navigation Tabs */}
      <nav className="bg-white border-b border-gray-200 sticky top-0 z-10">
        <div className="max-w-7xl mx-auto px-4">
          <div className="flex overflow-x-auto space-x-1">
            {tabs.map((tab) => (
              <button
                key={tab.id}
                onClick={() => setActiveTab(tab.id)}
                className={`px-4 py-3 whitespace-nowrap font-medium text-sm border-b-2 transition-colors ${
                  activeTab === tab.id
                    ? 'border-blue-600 text-blue-600'
                    : 'border-transparent text-gray-600 hover:text-gray-900'
                }`}
              >
                {tab.label}
              </button>
            ))}
          </div>
        </div>
      </nav>

      {/* Main Content */}
      <main className="max-w-7xl mx-auto px-4 py-8">
        {renderTab()}
      </main>

      {/* Footer */}
      <footer className="bg-gray-100 border-t border-gray-200 mt-12">
        <div className="max-w-7xl mx-auto px-4 py-4 text-sm text-gray-600">
          <p>API: http://localhost:5005 | Bridge built on sfera-api-main</p>
        </div>
      </footer>
    </div>
  )
}
