using UnityEngine;
using xpTURN.Klotho;
using xpTURN.Klotho.Core;
using xpTURN.Klotho.ECS;

namespace xpTURN.Samples.P2pSample
{
    public class P2pPlayerView : EntityView
    {
        [SerializeField] private Renderer _renderer;
        [SerializeField] private Color _color0 = new Color(0.18f, 0.42f, 0.87f);
        [SerializeField] private Color _color1 = new Color(0.87f, 0.30f, 0.18f);

        public int PlayerId { get; private set; } = -1;

        public override void OnActivate(FrameRef frame)
        {
            base.OnActivate(frame);

            if (frame.Frame.Has<PlayerComponent>(EntityRef))
            {
                ref readonly var p = ref frame.Frame.Get<PlayerComponent>(EntityRef);
                PlayerId = p.PlayerId;
                if (_renderer != null)
                {
                    _renderer.material.color = (PlayerId == 0) ? _color0 : _color1;
                }
            }
        }
    }
}
