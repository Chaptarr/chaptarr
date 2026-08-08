class SeriesEnrichmentService {
  constructor() {
    this.eventSource = null;
    this.reconnectAttempts = 0;
    this.maxReconnectAttempts = 5;
    this.reconnectDelay = 1000;
    this.isActive = false;
    this.onSeriesEnriched = null; // Callback to be set by the component
  }

  connect() {
    if (this.eventSource && this.eventSource.readyState !== EventSource.CLOSED) {
      return; // Already connected
    }

    this.isActive = true;
    const urlBase = window.Chaptarr.urlBase || '';
    const apiKey = window.Chaptarr.apiKey;
    const url = `${urlBase}/api/v1/enrichment/series/stream?apikey=${encodeURIComponent(apiKey)}`;

    console.log('[SeriesEnrichment] Connecting to SSE endpoint');

    this.eventSource = new EventSource(url);

    this.eventSource.addEventListener('connected', (event) => {
      console.log('[SeriesEnrichment] Connected:', event.data);
      this.reconnectAttempts = 0;
    });

    this.eventSource.addEventListener('series-enriched', (event) => {
      try {
        const data = JSON.parse(event.data);
        console.log('[SeriesEnrichment] Series enriched:', data.seriesId);
        this.handleSeriesEnriched(data);
      } catch (error) {
        console.error('[SeriesEnrichment] Error parsing enrichment data:', error);
      }
    });

    this.eventSource.addEventListener('enrichment-error', (event) => {
      try {
        const data = JSON.parse(event.data);
        console.error('[SeriesEnrichment] Enrichment error:', data);
      } catch (error) {
        console.error('[SeriesEnrichment] Error parsing error data:', error);
      }
    });

    this.eventSource.addEventListener('heartbeat', () => {
      // Just a keepalive, no action needed
    });

    this.eventSource.onerror = (error) => {
      console.error('[SeriesEnrichment] SSE error:', error);
      this.eventSource.close();
      
      if (this.isActive && this.reconnectAttempts < this.maxReconnectAttempts) {
        this.reconnectAttempts++;
        const delay = this.reconnectDelay * Math.pow(2, this.reconnectAttempts - 1);
        console.log(`[SeriesEnrichment] Reconnecting in ${delay}ms (attempt ${this.reconnectAttempts})`);
        setTimeout(() => this.connect(), delay);
      }
    };
  }

  disconnect() {
    this.isActive = false;
    if (this.eventSource) {
      console.log('[SeriesEnrichment] Disconnecting SSE');
      this.eventSource.close();
      this.eventSource = null;
    }
  }

  handleSeriesEnriched(data) {
    // Call the callback if it's set
    if (this.onSeriesEnriched) {
      this.onSeriesEnriched(data);
    }
  }

  setEnrichmentCallback(callback) {
    this.onSeriesEnriched = callback;
  }

  isConnected() {
    return this.eventSource && this.eventSource.readyState === EventSource.OPEN;
  }
}

// Create a singleton instance
const seriesEnrichmentService = new SeriesEnrichmentService();

export default seriesEnrichmentService;
