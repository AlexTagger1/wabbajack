﻿using System.IO;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace Wabbajack.Lib.CompilationSteps
{
    public class IgnoreVortex : ACompilationStep
    {
        private readonly VortexCompiler _vortex;

        public IgnoreVortex(ACompiler compiler) : base(compiler)
        {
            _vortex = (VortexCompiler) compiler;
        }

        public override async ValueTask<Directive> Run(RawSourceFile source)
        {
            if (source.AbsolutePath.Parent != _vortex.DownloadsFolder) return null;
            var result = source.EvolveTo<IgnoredDirectly>();
            result.Reason = "Ignored because it is a Vortex file";
            return result;

        }

        public override IState GetState()
        {
            return new State();
        }

        [JsonObject("IgnoreVortex")]
        public class State : IState
        {
            public ICompilationStep CreateStep(ACompiler compiler)
            {
                return new IgnoreVortex(compiler);
            }
        }
    }
}
