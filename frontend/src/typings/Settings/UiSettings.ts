export default interface UiSettings {
  experienceMode: 'simple' | 'advanced';
  theme: 'auto' | 'dark' | 'light';
  showRelativeDates: boolean;
  shortDateFormat: string;
  longDateFormat: string;
  timeFormat: string;
  addNewDefaultMediaType?: 'audiobook' | 'ebook' | 'both' | '';
}
