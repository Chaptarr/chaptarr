// webpack's DefinePlugin replaces `process.env.NODE_ENV` at build time, but
// `tsc` runs without DefinePlugin. Provide a minimal ambient type so type-
// checking of `process.env.NODE_ENV === 'production'` (e.g. in index.ts) passes.
declare const process: {
  env: {
    NODE_ENV?: string;
    [key: string]: string | undefined;
  };
};
