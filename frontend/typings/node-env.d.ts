// Node's ambient declarations provide `process`; augment its environment type
// for the value webpack's DefinePlugin replaces at build time.
declare namespace NodeJS {
  interface ProcessEnv {
    NODE_ENV?: string;
  }
}
