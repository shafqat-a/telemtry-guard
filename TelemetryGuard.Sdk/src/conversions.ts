import { state } from './state';

type Goal = {
  goalId: string;
  name: string;
  triggerType: 'time_on_page' | 'link_click' | 'button_click' | 'form_submitted';
  pagePaths: string[];
  selector?: string | null;
  minimumSeconds?: number | null;
};

const fired = new Set<string>();

function pathMatches(goal: Goal): boolean {
  const current = location.pathname.length > 1 ? location.pathname.replace(/\/+$/, '') : '/';
  return goal.pagePaths.some((p) => (p.length > 1 ? p.replace(/\/+$/, '') : '/') === current);
}

function emit(goal: Goal): void {
  try {
    if (fired.has(goal.goalId) || !pathMatches(goal)) return;
    fired.add(goal.goalId);
    const eventId = crypto.randomUUID();
    const payload = {
      k: state.cfg.siteKey,
      eventId,
      goalId: goal.goalId,
      sessionId: state.sid,
      visitId: state.visitId,
      pageUrl: location.href,
      occurredAt: new Date().toISOString(),
      pageToConversionMs: Math.max(0, Math.round(performance.now())),
    };
    void fetch(state.cfg.endpoint + '/i/conversion', {
      method: 'POST', body: JSON.stringify(payload), credentials: 'omit', keepalive: true,
      headers: { 'Content-Type': 'text/plain' },
    }).catch(() => { /* fire-and-forget */ });
  } catch { /* never affect host page */ }
}

export function installConversionGoals(goals: Goal[]): void {
  try {
    const active=goals.filter(pathMatches);
    for(const goal of active)
      if(goal.triggerType==='time_on_page'&&goal.minimumSeconds&&goal.minimumSeconds>0)
        window.setTimeout(()=>emit(goal),goal.minimumSeconds*1000);
    document.addEventListener('click',(event)=>{
      const target=event.target instanceof Element?event.target:null;
      if(!target) return;
      for(const goal of active)
        if((goal.triggerType==='link_click'||goal.triggerType==='button_click')&&goal.selector){
          try { if(target.closest(goal.selector)) emit(goal); } catch { /* invalid selector */ }
        }
    },{passive:true});
    document.addEventListener('submit',(event)=>{
      const target=event.target instanceof Element?event.target:null;
      if(!target) return;
      for(const goal of active)
        if(goal.triggerType==='form_submitted'&&goal.selector){
          try { if(target.matches(goal.selector)) emit(goal); } catch { /* invalid selector */ }
        }
    },{capture:true});
  } catch { /* never affect host page */ }
}
