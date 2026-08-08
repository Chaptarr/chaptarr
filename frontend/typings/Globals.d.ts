declare module '*.module.css';

interface Window {
  Chaptarr: {
    apiKey: string;
    instanceName: string;
    theme: string;
    urlBase: string;
    version: string;
    isProduction: boolean;
  };
}
